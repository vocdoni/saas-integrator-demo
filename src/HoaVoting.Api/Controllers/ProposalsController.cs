using System.Net;
using System.Text.Json;
using HoaVoting.Api.Authorization;
using HoaVoting.Api.Data;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Models;
using HoaVoting.Api.Services;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Api.Controllers;

[Authorize]
[Route("api/associations/{associationId:int}/proposals")]
public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiControllerBase
{
    private const int MaxQuestions = 15;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(90);

    /// <summary>
    /// Create a proposal as a Vocdoni multi-question voting process (saas-backend #571): author the
    /// process (census inline over the current homeowners, one election per question), save the local
    /// row as Draft, then run the publish routine (<see cref="ProposalSync.PublishAsync"/>). 201 + Open
    /// when the publish job finished within the bounded poll, 202 + Draft while it is still running (the
    /// reconcile promotes it later), 502 when upstream refused or the job failed.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProposalResponse>> Create(int associationId, CreateProposalRequest req, CancellationToken ct)
    {
        var (assoc, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;
        if (req.Questions is null || req.Questions.Count == 0) return BadRequest("A proposal needs at least one question.");
        // Upstream limits (saas-backend #819): 15 questions per process, 90-day processes.
        if (req.Questions.Count > MaxQuestions) return BadRequest($"A proposal can have at most {MaxQuestions} questions.");
        if (req.EndDate - req.StartDate > MaxDuration) return BadRequest($"A proposal can run for at most {MaxDuration.TotalDays} days.");
        if (req.Questions.Any(q => q.Choices.Count < 2)) return BadRequest("Each question needs at least two choices.");
        // An open "Other" choice (free-text voter memo, #577) is single-choice-only, at most one per question.
        foreach (var (q, qi) in req.Questions.Select((q, i) => (q, i)))
        {
            var opens = q.Choices.Count(c => c.Open);
            if (opens > 1) return BadRequest($"Question {qi + 1}: at most one choice can be an open 'Other' answer.");
            if (opens == 1 && q.Kind != VotingType.Single)
                return BadRequest($"Question {qi + 1}: an open 'Other' choice is only supported on single-choice questions.");
            if (q.Kind == VotingType.Cumulative && (q.Budget is not > 0 || q.CostExponent is not (1 or 2)))
                return BadRequest($"Question {qi + 1}: cumulative voting needs a budget > 0 and a cost exponent of 1 (linear) or 2 (quadratic).");
        }

        var org = assoc!.VocdoniOrgAddress;
        var members = await vocdoni.ListMembersAsync(org, ct);
        var memberIds = members.Where(m => m.Id is not null).Select(m => m.Id!).ToList();
        if (memberIds.Count == 0) return BadRequest("Add homeowners before creating a proposal.");

        var request = new CreateVotingProcessRequest
        {
            OrgAddress = org,
            // Auth-only census over the current homeowners (member number, plus the member's password when
            // required — saas-backend #760; members without one land in missingData). No 2FA.
            // Anonymous (saas-backend #641) swaps the CSP to blind signatures; omitted when false.
            Census = new CensusSpec
            {
                AuthFields = req.RequirePassword ? ["memberNumber", "password"] : ["memberNumber"],
                MemberIds = memberIds,
                Anonymous = req.Anonymous ? true : null,
            },
            Title = Lang(req.Title),
            Description = Lang(req.Description),
            StartDate = req.StartDate.UtcDateTime.ToString("o"),
            EndDate = req.EndDate.UtcDateTime.ToString("o"),
            Questions = req.Questions.Select(ToQuestionRequest).ToList(),
        };

        var created = await vocdoni.CreateVotingProcessAsync(request, ct);

        // From here on upstream holds a draft: record it before publishing (and whatever happens next)
        // with CancellationToken.None, so a client disconnect cannot leave it untracked.
        var proposal = new Proposal
        {
            AssociationId = assoc.Id,
            Title = req.Title,
            Description = req.Description,
            VocdoniProcessId = created.ProcessId!,
            Anonymous = req.Anonymous,
            RequirePassword = req.RequirePassword,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            Status = ProposalStatus.Draft,
            CreatedAt = DateTimeOffset.UtcNow,
            Questions = req.Questions.Select((q, i) => new ProposalQuestion
            {
                Order = i,
                Title = q.Title,
                ChoicesJson = JsonSerializer.Serialize(q.Choices.Select(c => c.Title)),
                OpenChoiceIndex = q.Choices.FindIndex(c => c.Open),
                Kind = q.Kind,
                Budget = q.Kind == VotingType.Cumulative ? q.Budget : null,
                CostExponent = q.Kind == VotingType.Cumulative ? q.CostExponent : null,
            }).ToList(),
        };
        db.Proposals.Add(proposal);
        await db.SaveChangesAsync(CancellationToken.None);

        var outcome = await ProposalSync.PublishAsync(db, vocdoni, proposal, deleteOnRefusal: true, ct);
        var body = ToResponse(proposal, missingData: created.MissingData);
        return outcome switch
        {
            PublishOutcome.Open => CreatedAtAction(nameof(Get), new { associationId, id = proposal.Id }, body),
            PublishOutcome.Pending => Accepted(body),
            _ => UpstreamFailure(proposal.PublishError!),
        };
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProposalResponse>>> List(int associationId, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var items = await db.Proposals.Include(p => p.Questions)
            .Where(p => p.AssociationId == associationId).OrderBy(p => p.Id).ToListAsync(ct);
        var results = await HydrateAsync(items, ct);
        return items.Select(p => ToResponse(p, results.GetValueOrDefault(p.Id))).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProposalResponse>> Get(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await FindAsync(associationId, id, ct);
        if (p is null) return NotFound();
        var results = await HydrateAsync([p], ct);
        return ToResponse(p, results.GetValueOrDefault(p.Id));
    }

    /// <summary>
    /// Delete a proposal stuck publishing (Draft) or whose publish failed (Failed): upstream draft first
    /// (200 or 404 → the local row goes too, 204). Any other upstream answer — typically 409 because a
    /// publish submitted elections — propagates as 502 and the row stays (Retry publish resolves those).
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await FindAsync(associationId, id, ct);
        if (p is null) return NotFound();
        if (p.Status is not (ProposalStatus.Draft or ProposalStatus.Failed))
            return Conflict("Only a proposal that is still publishing or failed to publish can be deleted.");

        try
        {
            await vocdoni.DeleteVotingProcessAsync(p.VocdoniProcessId, ct);
        }
        catch (VocdoniApiException e) when (e.Status == HttpStatusCode.NotFound) { /* already gone upstream */ }

        db.Proposals.Remove(p);
        await db.SaveChangesAsync(CancellationToken.None);
        return NoContent();
    }

    /// <summary>
    /// Retry the publish of a Failed proposal. Upstream adopts any election a previous attempt already
    /// mined, at no new charge. Same routine as create, except a refusal marks it Failed again (no delete).
    /// </summary>
    [HttpPost("{id:int}/publish")]
    public async Task<ActionResult<ProposalResponse>> Publish(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await FindAsync(associationId, id, ct);
        if (p is null) return NotFound();
        if (p.Status != ProposalStatus.Failed) return Conflict("Only a proposal whose publish failed can be published again.");

        // Drop the old job too: the reconcile must not fail the retry from the previous attempt's job.
        p.Status = ProposalStatus.Draft;
        p.PublishError = null;
        p.PublishJobId = null;
        await db.SaveChangesAsync(CancellationToken.None);

        return await ProposalSync.PublishAsync(db, vocdoni, p, deleteOnRefusal: false, ct) switch
        {
            PublishOutcome.Open => ToResponse(p),
            PublishOutcome.Pending => Accepted(ToResponse(p)),
            _ => UpstreamFailure(p.PublishError!),
        };
    }

    /// <summary>
    /// Close voting: enqueue the on-chain end of every question and return immediately (the change is
    /// async). We don't optimistically mark the proposal Closed — the real status is reconciled from
    /// the process read once the end tx mines and the questions report ended. Open proposals only.
    /// </summary>
    [HttpPost("{id:int}/close")]
    public async Task<ActionResult> Close(int associationId, int id, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
        if (p is null) return NotFound();
        if (p.Status != ProposalStatus.Open) return Conflict("Only an open proposal can be closed.");

        await vocdoni.SetQuestionsStatusAsync(p.VocdoniProcessId, "ended", null, ct);
        return Accepted();
    }

    /// <summary>
    /// Extend an open proposal's end date (saas-backend #844). 200 with the new end once the job
    /// completed; 202 with the old end while it is still running (the reconcile copies upstream's end
    /// date later); 502 when it failed. Upstream enforces the 5-minute / 90-day rules (400 → 502).
    /// </summary>
    [HttpPut("{id:int}/end-date")]
    public async Task<ActionResult<ProposalResponse>> SetEndDate(int associationId, int id, SetEndDateRequest req, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await FindAsync(associationId, id, ct);
        if (p is null) return NotFound();
        if (p.Status != ProposalStatus.Open) return BadRequest("Only an open proposal's end date can be extended.");
        if (req.EndDate <= p.EndDate) return BadRequest("The new end date must be later than the current one.");

        var jobId = await vocdoni.SetProcessEndDateAsync(p.VocdoniProcessId, req.EndDate, ct);
        var (state, jobError) = await vocdoni.WaitForJobAsync(jobId, ct);
        switch (state)
        {
            case JobState.Completed:
                p.EndDate = req.EndDate;
                await db.SaveChangesAsync(CancellationToken.None);
                return ToResponse(p);
            case JobState.Pending:
                return Accepted(ToResponse(p));
            default:
                return UpstreamFailure(string.IsNullOrEmpty(jobError) ? "The end-date change failed." : jobError);
        }
    }

    /// <summary>
    /// Replace a question's voter-eligibility restriction on a live process (saas-backend #621).
    /// The body carries the COMPLETE desired member-id list; [] reopens the question to the whole
    /// census. 409 when the change would strip a voter the CSP already signed for (code 40173) —
    /// those members are returned so the UI can name them.
    /// </summary>
    [HttpPut("{id:int}/questions/{questionId:int}/eligibility")]
    public async Task<ActionResult<EligibilityResponse>> SetEligibility(
        int associationId, int id, int questionId, SetEligibilityRequest req, CancellationToken ct)
    {
        var (_, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var p = await db.Proposals.Include(x => x.Questions)
            .SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
        var question = p?.Questions.SingleOrDefault(q => q.Id == questionId);
        if (p is null || question is null) return NotFound();
        if (string.IsNullOrEmpty(question.UpstreamId)) return Conflict("The question is not published yet.");

        // The upstream endpoint is keyed by the Vocdoni question id, which we don't store — resolve it
        // from the process read by matching the on-chain election id.
        var proc = await vocdoni.GetVotingProcessAsync(p.VocdoniProcessId, ct);
        var upstreamQuestionId = proc.Questions.SingleOrDefault(q => q.UpstreamId == question.UpstreamId)?.Id;
        if (string.IsNullOrEmpty(upstreamQuestionId))
            return Conflict("The question was not found on the upstream process.");

        try
        {
            var res = await vocdoni.SetQuestionCensusAsync(p.VocdoniProcessId, upstreamQuestionId!, req.MemberIds, ct);
            return new EligibilityResponse(res.Eligible, res.Added, res.Removed);
        }
        catch (VocdoniApiException e) when (e.Status == System.Net.HttpStatusCode.Conflict)
        {
            return Conflict(ParseEligibilityConflict(e.Body));
        }
    }

    // Upstream 409 body: {code, error, data:{signedMemberIds:[...]}} — 40173 = voter already signed for.
    // Every read is guarded by ValueKind: the body is upstream input, and JsonElement getters throw
    // InvalidOperationException (not JsonException) on a mismatched kind — an unexpected-but-valid
    // JSON shape must still yield a 409, never a 500.
    internal static EligibilityConflictResponse ParseEligibilityConflict(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("code", out var c)
                && c.ValueKind == JsonValueKind.Number
                && c.TryGetInt32(out var parsed) ? parsed : 0;
            var msg = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var er)
                && er.ValueKind == JsonValueKind.String ? er.GetString() ?? "" : "";
            if (code == 40173)
            {
                var ids = root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("signedMemberIds", out var arr)
                    && arr.ValueKind == JsonValueKind.Array
                        ? arr.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString()!).Where(s => s != "").ToList()
                        : [];
                return new EligibilityConflictResponse(
                    "These members have already voted (or hold a ballot signature) and cannot lose eligibility while the question is running.",
                    ids);
            }
            return new EligibilityConflictResponse(msg == "" ? "The eligibility change was rejected." : msg, []);
        }
        catch (JsonException)
        {
            return new EligibilityConflictResponse("The eligibility change was rejected.", []);
        }
    }

    // Reconcile each proposal with its process read (ProposalSync.ReconcileAsync: settle drafts, end date,
    // question status, Closed) and return each upstream question (tally + eligibility, #621) keyed by
    // on-chain id for the response. Best-effort per proposal; one save for the batch.
    private async Task<Dictionary<int, Dictionary<string, VotingProcessQuestion>>> HydrateAsync(
        IReadOnlyList<Proposal> proposals, CancellationToken ct)
    {
        var map = new Dictionary<int, Dictionary<string, VotingProcessQuestion>>();
        foreach (var p in proposals)
            map[p.Id] = await ProposalSync.ReconcileAsync(vocdoni, p, ct);
        await ProposalSync.SaveAsync(db);
        return map;
    }

    // Map the demo's UI kind to a #571 question, using the named types only (saas-backend #638).
    // The backend derives the on-chain ballot protocol from type + typeSetup; supplying a protocol that
    // contradicts the named type is a 400, and ranked rejects any typeSetup at all (choices define it).
    internal static VotingProcessQuestionRequest ToQuestionRequest(QuestionInput q)
    {
        var n = q.Choices.Count;
        var choices = q.Choices.Select((c, i) => new VocdoniChoice { Title = Lang(c.Title), Value = (uint)i, OpenValue = c.Open }).ToList();
        return q.Kind switch
        {
            VotingType.Single => new VotingProcessQuestionRequest
            {
                Title = Lang(q.Title), Choices = choices, Type = "singlechoice",
                TypeSetup = new QuestionTypeSetup { MinChoices = 1, MaxChoices = 1 },
            },
            VotingType.Multiple => new VotingProcessQuestionRequest
            {
                Title = Lang(q.Title), Choices = choices, Type = "multichoice",
                TypeSetup = new QuestionTypeSetup { MinChoices = 1, MaxChoices = (uint)n },
            },
            VotingType.Ranked => new VotingProcessQuestionRequest
            {
                Title = Lang(q.Title), Choices = choices, Type = "ranked",
            },
            VotingType.Cumulative => new VotingProcessQuestionRequest
            {
                Title = Lang(q.Title), Choices = choices, Type = "cumulative",
                TypeSetup = new QuestionTypeSetup { Budget = (uint)q.Budget!, CostExponent = (uint)q.CostExponent! },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(q.Kind)),
        };
    }

    private static Dictionary<string, string> Lang(string text) => new() { ["default"] = text };

    private static ProposalResponse ToResponse(
        Proposal p, IReadOnlyDictionary<string, VotingProcessQuestion>? upstream = null, List<string>? missingData = null)
    {
        var questions = p.Questions.OrderBy(q => q.Order).Select(q =>
        {
            VotingProcessQuestion? pq = null;
            if (upstream is not null && !string.IsNullOrEmpty(q.UpstreamId)) upstream.TryGetValue(q.UpstreamId, out pq);
            var qr = pq?.Results;
            return new QuestionResponse(
                q.Id, q.Order, q.Title,
                JsonSerializer.Deserialize<List<string>>(q.ChoicesJson) ?? [],
                q.Kind, q.OpenChoiceIndex, q.UpstreamId, q.Status,
                qr?.VoteCount ?? 0, qr?.MaxVoters ?? 0, qr?.Results, qr?.Memos,
                q.Budget, q.CostExponent, pq?.EligibleMemberIds);
        }).ToList();
        return new ProposalResponse(
            p.Id, p.AssociationId, p.Title, p.Description,
            p.Status.ToString(), p.VocdoniProcessId, p.StartDate, p.EndDate, p.CreatedAt, questions, p.Anonymous,
            p.RequirePassword, p.PublishError, missingData);
    }

    // Same body shape as Program.cs's mapping of a thrown upstream failure.
    private ObjectResult UpstreamFailure(string detail) =>
        StatusCode(StatusCodes.Status502BadGateway, new { error = "vocdoni_upstream", detail });

    private Task<Proposal?> FindAsync(int associationId, int id, CancellationToken ct) =>
        db.Proposals.Include(x => x.Questions).SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);

    private async Task<(Association?, ActionResult?)> ResolveAsync(int associationId, CancellationToken ct)
    {
        var assoc = await db.Associations.FindAsync([associationId], ct);
        if (assoc is null) return (null, NotFound());
        if (!AssociationAccess.CanAccess(CurrentRole, CurrentUserId, assoc)) return (null, Forbid());
        return (assoc, null);
    }
}
