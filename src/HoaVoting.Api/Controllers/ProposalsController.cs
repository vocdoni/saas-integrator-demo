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
    /// Create a proposal: snapshot current homeowners into a census, publish it, then create and
    /// publish a Vocdoni voting process. Voters cast ballots client-side via the Vocdoni JS SDK.
    /// ponytail: census publish + process publish/status are async jobs server-side; for a live
    /// deployment poll GET /jobs/{jobId} between steps. Demo runs them sequentially.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProposalResponse>> Create(int associationId, CreateProposalRequest req, CancellationToken ct)
    {
        var (assoc, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;
        if (req.Choices.Count < 2) return BadRequest("A proposal needs at least two choices.");

        var org = assoc!.VocdoniOrgAddress;

        var members = await vocdoni.ListMembersAsync(org, ct);
        var memberCount = members.Count(m => m.Id is not null);
        if (memberCount == 0) return BadRequest("Add homeowners before creating a proposal.");

        // Auth-only CSP census: members authenticate by member number alone (no 2FA). We publish via a
        // member group — that path supports auth-only censuses, where the plain /census/{id}/publish
        // would reject them ("census type not found"). The group also populates census participants
        // from its members, so no separate add-participants call is needed.
        List<string> authFields = ["memberNumber"];
        var groupId = await vocdoni.CreateAllMembersGroupAsync(org, $"Proposal: {req.Title}", ct);
        var censusId = await vocdoni.CreateCensusAsync(org, authFields, ct);
        await vocdoni.PublishCensusGroupAsync(censusId, groupId, authFields, ct);

        var process = new CreateProcessRequest
        {
            OrgAddress = org,
            CensusId = censusId,
            Metadata = new Dictionary<string, object> { ["title"] = req.Title, ["description"] = req.Description },
            ElectionParams = new ElectionParams
            {
                Title = Lang(req.Title),
                Description = Lang(req.Description),
                StartDate = req.StartDate.UtcDateTime.ToString("o"),
                EndDate = req.EndDate.UtcDateTime.ToString("o"),
                MaxCensusSize = memberCount,
                ElectionType = new ElectionType { Autostart = true, Interruptible = true },
                VoteType = req.VotingType switch
                {
                    // Single: one field whose value is the chosen index (0..N-1).
                    VotingType.Single => new VoteType { MaxCount = 1, MaxValue = req.Choices.Count - 1 },
                    // Approval: one 0/1 field per option; multiple 1s allowed (uniqueChoices MUST be
                    // false, else repeating a value — e.g. two selected options — is rejected).
                    VotingType.Multiple => new VoteType { MaxCount = req.Choices.Count, MaxValue = 1, UniqueChoices = false },
                    // Ranked (linear-weighted): one field per option, each a unique rank value 0..N-1.
                    VotingType.Ranked => new VoteType { MaxCount = req.Choices.Count, MaxValue = req.Choices.Count - 1, UniqueChoices = true },
                    _ => throw new ArgumentOutOfRangeException(nameof(req.VotingType)),
                },
                Questions =
                [
                    new ElectionQuestion
                    {
                        Title = Lang(req.Title),
                        Description = Lang(req.Description),
                        Choices = req.Choices
                            .Select((c, i) => new ElectionChoice { Title = Lang(c.Title), Value = i })
                            .ToList(),
                    },
                ],
            },
        };

        // POST /process returns the 24-hex ProcessID — the one handle the integrator needs. It
        // addresses status/results/metadata (saas-backend #551) and the bundle (#554).
        var processId = await vocdoni.CreateProcessAsync(process, ct);
        // Publish on-chain and wait until it's live (the bundle requires a published process).
        await vocdoni.PublishProcessAsync(processId, ct);
        var bundleId = await vocdoni.CreateBundleAsync(censusId, [processId], ct);

        var proposal = new Proposal
        {
            AssociationId = assoc.Id,
            Title = req.Title,
            Description = req.Description,
            VocdoniCensusId = censusId,
            VocdoniProcessId = processId,
            VocdoniBundleId = bundleId,
            ChoicesJson = JsonSerializer.Serialize(req.Choices.Select(c => c.Title)),
            VotingType = req.VotingType,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            Status = ProposalStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
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

        var items = await db.Proposals.Where(p => p.AssociationId == associationId).OrderBy(p => p.Id).ToListAsync(ct);
        await ReconcileStatusesAsync(items, ct);
        return items.Select(ToResponse).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProposalResponse>> Get(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
        if (p is null) return NotFound();
        await ReconcileStatusesAsync([p], ct);
        return ToResponse(p);
    }

    /// <summary>Close voting on a proposal (ends the Vocdoni process).</summary>
    [HttpPost("{id:int}/close")]
    public async Task<ActionResult> Close(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
        if (p is null) return NotFound();

        await vocdoni.SetProcessStatusAsync(p.VocdoniProcessId, "ended", ct);
        p.Status = ProposalStatus.Closed;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:int}/results")]
    public async Task<ActionResult<ProposalResultsResponse>> Results(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
        if (p is null) return NotFound();

        var r = await vocdoni.GetResultsAsync(p.VocdoniProcessId, ct);
        // Census size lives on the process detail, not the results payload — fetch it best-effort.
        int? censusSize = null;
        try { censusSize = await vocdoni.GetCensusSizeAsync(p.VocdoniProcessId, ct); }
        catch (VocdoniApiException) { /* leave null; the tally falls back client-side */ }
        return new ProposalResultsResponse(p.VocdoniProcessId, r.Status, r.FinalResults, r.VoteCount, r.Results, censusSize);
    }

    // A proposal whose Vocdoni process has ended on-chain (RESULTS/ENDED/CANCELED) or whose end date
    // has passed is closed, even if it was never explicitly closed here — e.g. an owner close that
    // ended the process on-chain but failed to persist, or natural expiry. Reconcile the stored status
    // (best-effort, Open proposals only) so every consumer of the API sees the real state.
    private async Task ReconcileStatusesAsync(IReadOnlyList<Proposal> proposals, CancellationToken ct)
    {
        var changed = false;
        foreach (var p in proposals.Where(x => x.Status == ProposalStatus.Open))
        {
            string? onchain = null;
            try { onchain = (await vocdoni.GetResultsAsync(p.VocdoniProcessId, ct)).Status; }
            catch (VocdoniApiException) { /* upstream unreachable — leave as-is */ }
            catch (HttpRequestException) { }

            var oc = (onchain ?? "").ToUpperInvariant();
            if (oc is "RESULTS" or "ENDED" or "CANCELED" || p.EndDate < DateTimeOffset.UtcNow)
            {
                p.Status = ProposalStatus.Closed;
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private static Dictionary<string, string> Lang(string text) => new() { ["default"] = text };

    private static ProposalResponse ToResponse(Proposal p) => new(
        p.Id, p.AssociationId, p.Title, p.Description,
        JsonSerializer.Deserialize<List<string>>(p.ChoicesJson) ?? [],
        p.VotingType,
        p.Status.ToString(), p.VocdoniProcessId, p.VocdoniCensusId, p.VocdoniBundleId,
        p.StartDate, p.EndDate, p.CreatedAt);

    private async Task<(Association?, ActionResult?)> ResolveAsync(int associationId, CancellationToken ct)
    {
        var assoc = await db.Associations.FindAsync([associationId], ct);
        if (assoc is null) return (null, NotFound());
        if (!AssociationAccess.CanAccess(CurrentRole, CurrentUserId, assoc)) return (null, Forbid());
        return (assoc, null);
    }
}
