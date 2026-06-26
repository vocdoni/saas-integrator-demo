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

        // CSP-based census: members authenticate by member number; email 2FA is opt-in. We publish
        // via a member group — that path supports auth-only (no-2FA) censuses, where the plain
        // /census/{id}/publish would reject them ("census type not found"). The group also populates
        // census participants from its members, so no separate add-participants call is needed.
        List<string> authFields = ["memberNumber"];
        List<string> twoFaFields = req.TwoFactorAuth ? ["email"] : [];
        var groupId = await vocdoni.CreateAllMembersGroupAsync(org, $"Proposal: {req.Title}", ct);
        var censusId = await vocdoni.CreateCensusAsync(org, authFields, twoFaFields, ct);
        await vocdoni.PublishCensusGroupAsync(censusId, groupId, authFields, twoFaFields, ct);

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
                VoteType = req.AllowMultiple
                    ? new VoteType { MaxCount = req.Choices.Count, MaxValue = 1, UniqueChoices = true }
                    : new VoteType { MaxCount = 1, MaxValue = req.Choices.Count - 1 },
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
        return items.Select(ToResponse).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProposalResponse>> Get(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
        return p is null ? NotFound() : ToResponse(p);
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
        return new ProposalResultsResponse(p.VocdoniProcessId, r.Status, r.FinalResults, r.VoteCount, r.Results);
    }

    private static Dictionary<string, string> Lang(string text) => new() { ["default"] = text };

    private static ProposalResponse ToResponse(Proposal p) => new(
        p.Id, p.AssociationId, p.Title, p.Description,
        JsonSerializer.Deserialize<List<string>>(p.ChoicesJson) ?? [],
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
