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

        // Live per-question status + tally, best-effort (the page still renders if unreachable).
        var byUpstream = new Dictionary<string, VotingProcessQuestionResults>();
        try
        {
            var res = await vocdoni.GetVotingProcessResultsAsync(processId, ct);
            foreach (var qr in res.Questions)
                if (!string.IsNullOrEmpty(qr.UpstreamId)) byUpstream[qr.UpstreamId!] = qr;
        }
        catch (VocdoniApiException) { /* serve stored values */ }
        catch (HttpRequestException) { }

        var opts = vocdoniOptions.Value;
        // The page calls the SaaS API from the browser, so hand it a browser-reachable URL (PublicBaseUrl),
        // which may differ from the backend's own BaseUrl (local Docker: host.docker.internal vs localhost).
        var apiUrl = string.IsNullOrEmpty(opts.PublicBaseUrl) ? opts.BaseUrl : opts.PublicBaseUrl;
        var questions = p.Questions.OrderBy(q => q.Order).Select(q =>
        {
            VotingProcessQuestionResults? qr = null;
            if (!string.IsNullOrEmpty(q.UpstreamId)) byUpstream.TryGetValue(q.UpstreamId, out qr);
            return new PublicQuestion(
                q.Id, q.Order, q.Title,
                JsonSerializer.Deserialize<List<string>>(q.ChoicesJson) ?? [],
                q.Kind, q.UpstreamId, qr?.Status ?? q.Status, qr?.VoteCount ?? 0, qr?.Results);
        }).ToList();

        return new VotingInfoResponse(
            p.VocdoniProcessId, apiUrl, p.ChainId, p.Title, p.Description,
            p.StartDate, p.EndDate, p.Status.ToString(), questions);
    }
}
