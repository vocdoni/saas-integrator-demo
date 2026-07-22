using System.Text.Json;
using HoaVoting.Api.Data;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoaVoting.Api.Controllers;

/// <summary>Public, read-only voting-page data for a multi-question process (#571). No auth.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/processes")]
public class VotingController(AppDbContext db, IVocdoniClient vocdoni, IOptions<VocdoniOptions> vocdoniOptions) : ControllerBase
{
    [HttpGet("{processId}")]
    public async Task<ActionResult<VotingInfoResponse>> Get(string processId, CancellationToken ct)
    {
        var p = await db.Proposals.Include(x => x.Questions)
            .SingleOrDefaultAsync(x => x.VocdoniProcessId == processId, ct);
        if (p is null) return NotFound();

        // Live per-question status + inline tally (saas-backend #596), best-effort (page still renders if unreachable).
        var byUpstream = new Dictionary<string, VotingProcessQuestion>();
        try
        {
            var proc = await vocdoni.GetVotingProcessAsync(processId, ct);
            foreach (var q in proc.Questions)
                if (!string.IsNullOrEmpty(q.UpstreamId)) byUpstream[q.UpstreamId!] = q;
        }
        catch (VocdoniApiException) { /* serve stored values */ }
        catch (HttpRequestException) { }

        var opts = vocdoniOptions.Value;
        // The page calls the SaaS API from the browser, so hand it the browser-reachable URL (BrowserUrl),
        // which may differ from the backend's own ServerUrl (local Docker: host.docker.internal vs localhost).
        var apiUrl = string.IsNullOrEmpty(opts.BrowserUrl) ? opts.ServerUrl : opts.BrowserUrl;
        var questions = p.Questions.OrderBy(q => q.Order).Select(q =>
        {
            VotingProcessQuestion? pq = null;
            if (!string.IsNullOrEmpty(q.UpstreamId)) byUpstream.TryGetValue(q.UpstreamId, out pq);
            var qr = pq?.Results;
            return new PublicQuestion(
                q.Id, q.Order, q.Title,
                JsonSerializer.Deserialize<List<string>>(q.ChoicesJson) ?? [],
                q.Kind, q.UpstreamId, pq?.Status ?? q.Status, qr?.VoteCount ?? 0, qr?.MaxVoters ?? 0, qr?.Results);
        }).ToList();

        return new VotingInfoResponse(
            p.VocdoniProcessId, apiUrl, p.ChainId, p.Title, p.Description,
            p.StartDate, p.EndDate, p.Status.ToString(), questions);
    }
}
