using System.Text.Json;
using HoaVoting.Api.Authorization;
using HoaVoting.Api.Data;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Models;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Api.Controllers;

[Authorize]
[Route("api/associations/{associationId:int}/proposals")]
public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiControllerBase
{
    /// <summary>
    /// Create a proposal as a Vocdoni multi-question voting process (saas-backend #571): author the
    /// process (census inline over the current homeowners, one election per question), publish it
    /// (async batch), then read it back to capture each question's on-chain election id + status.
    /// Voters cast per question client-side.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProposalResponse>> Create(int associationId, CreateProposalRequest req, CancellationToken ct)
    {
        var (assoc, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;
        if (req.Questions is null || req.Questions.Count == 0) return BadRequest("A proposal needs at least one question.");
        if (req.Questions.Any(q => q.Choices.Count < 2)) return BadRequest("Each question needs at least two choices.");

        var org = assoc!.VocdoniOrgAddress;
        var members = await vocdoni.ListMembersAsync(org, ct);
        var memberIds = members.Where(m => m.Id is not null).Select(m => m.Id!).ToList();
        if (memberIds.Count == 0) return BadRequest("Add homeowners before creating a proposal.");

        var request = new CreateVotingProcessRequest
        {
            OrgAddress = org,
            // Auth-only census over the current homeowners (authenticate by member number, no 2FA).
            Census = new CensusSpec { AuthFields = ["memberNumber"], MemberIds = memberIds },
            Title = Lang(req.Title),
            Description = Lang(req.Description),
            StartDate = req.StartDate.UtcDateTime.ToString("o"),
            EndDate = req.EndDate.UtcDateTime.ToString("o"),
            Questions = req.Questions.Select(ToQuestionRequest).ToList(),
        };

        var processId = await vocdoni.CreateVotingProcessAsync(request, ct);
        await vocdoni.PublishVotingProcessAsync(processId, ct);
        // Read back to capture each question's on-chain id + status (assigned during the batch publish).
        var published = await vocdoni.GetVotingProcessAsync(processId, ct);

        var proposal = new Proposal
        {
            AssociationId = assoc.Id,
            Title = req.Title,
            Description = req.Description,
            VocdoniProcessId = processId,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            Status = ProposalStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            Questions = req.Questions.Select((q, i) => new ProposalQuestion
            {
                Order = i,
                Title = q.Title,
                ChoicesJson = JsonSerializer.Serialize(q.Choices.Select(c => c.Title)),
                Kind = q.Kind,
                UpstreamId = published.Questions.ElementAtOrDefault(i)?.UpstreamId ?? "",
                Status = published.Questions.ElementAtOrDefault(i)?.Status ?? "",
            }).ToList(),
        };
        db.Proposals.Add(proposal);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { associationId, id = proposal.Id }, ToResponse(proposal));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProposalResponse>>> List(int associationId, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var items = await db.Proposals.Include(p => p.Questions)
            .Where(p => p.AssociationId == associationId).OrderBy(p => p.Id).ToListAsync(ct);
        await ReconcileAsync(items, ct);
        return items.Select(ToResponse).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProposalResponse>> Get(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await db.Proposals.Include(x => x.Questions)
            .SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
        if (p is null) return NotFound();
        await ReconcileAsync([p], ct);
        return ToResponse(p);
    }

    /// <summary>Close voting: end every question of the process.</summary>
    [HttpPost("{id:int}/close")]
    public async Task<ActionResult> Close(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
        if (p is null) return NotFound();

        await vocdoni.SetQuestionsStatusAsync(p.VocdoniProcessId, "ended", null, ct);
        p.Status = ProposalStatus.Closed;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Refresh each proposal's questions (on-chain upstreamId + status) from the process read, and mark
    // the proposal Closed once every published question has ended (or its end date has passed). Best-
    // effort per not-yet-Closed proposal; the SaaS has no results endpoint for questions in #571.
    private async Task ReconcileAsync(IReadOnlyList<Proposal> proposals, CancellationToken ct)
    {
        var changed = false;
        foreach (var p in proposals.Where(x => x.Status != ProposalStatus.Closed))
        {
            VotingProcessResponse? proc = null;
            try { proc = await vocdoni.GetVotingProcessAsync(p.VocdoniProcessId, ct); }
            catch (VocdoniApiException) { /* upstream unreachable — leave as-is */ }
            catch (HttpRequestException) { }

            if (proc is not null)
                foreach (var q in p.Questions)
                {
                    var h = proc.Questions.ElementAtOrDefault(q.Order);
                    if (h is null) continue;
                    if (!string.IsNullOrEmpty(h.UpstreamId) && h.UpstreamId != q.UpstreamId) { q.UpstreamId = h.UpstreamId!; changed = true; }
                    if (!string.IsNullOrEmpty(h.Status) && h.Status != q.Status) { q.Status = h.Status!; changed = true; }
                }

            var ended = (p.Questions.Count > 0 && p.Questions.All(q => IsEnded(q.Status))) || p.EndDate < DateTimeOffset.UtcNow;
            if (ended) { p.Status = ProposalStatus.Closed; changed = true; }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private static bool IsEnded(string status) => status.ToUpperInvariant() is "ENDED" or "CANCELED" or "RESULTS";

    // Map the demo's UI kind to a #571 question. singlechoice/multichoice are named types; ranked uses
    // the raw ballotProtocol override (linear-weighted: each option gets a unique rank value 0..n-1).
    private static VotingProcessQuestionRequest ToQuestionRequest(QuestionInput q)
    {
        var n = q.Choices.Count;
        var choices = q.Choices.Select((c, i) => new VocdoniChoice { Title = Lang(c.Title), Value = (uint)i }).ToList();
        return q.Kind switch
        {
            VotingType.Single => new VotingProcessQuestionRequest
            {
                Title = Lang(q.Title), Choices = choices, Type = "singlechoice",
                TypeSetup = new QuestionTypeSetup { MinChoices = 1, MaxChoices = 1, UniqueChoices = false },
            },
            VotingType.Multiple => new VotingProcessQuestionRequest
            {
                Title = Lang(q.Title), Choices = choices, Type = "multichoice",
                TypeSetup = new QuestionTypeSetup { MinChoices = 1, MaxChoices = (uint)n, UniqueChoices = false },
            },
            VotingType.Ranked => new VotingProcessQuestionRequest
            {
                Title = Lang(q.Title), Choices = choices, Type = "singlechoice",
                TypeSetup = new QuestionTypeSetup { MinChoices = 1, MaxChoices = 1, UniqueChoices = false },
                BallotProtocol = new BallotProtocol { MaxCount = n, MaxValue = n - 1, UniqueValues = true },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(q.Kind)),
        };
    }

    private static Dictionary<string, string> Lang(string text) => new() { ["default"] = text };

    private static ProposalResponse ToResponse(Proposal p) => new(
        p.Id, p.AssociationId, p.Title, p.Description,
        p.Status.ToString(), p.VocdoniProcessId, p.StartDate, p.EndDate, p.CreatedAt,
        p.Questions.OrderBy(q => q.Order).Select(q => new QuestionResponse(
            q.Id, q.Order, q.Title,
            JsonSerializer.Deserialize<List<string>>(q.ChoicesJson) ?? [],
            q.Kind, q.UpstreamId, q.Status)).ToList());

    private async Task<(Association?, ActionResult?)> ResolveAsync(int associationId, CancellationToken ct)
    {
        var assoc = await db.Associations.FindAsync([associationId], ct);
        if (assoc is null) return (null, NotFound());
        if (!AssociationAccess.CanAccess(CurrentRole, CurrentUserId, assoc)) return (null, Forbid());
        return (assoc, null);
    }
}
