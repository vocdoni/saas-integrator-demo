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

        // Refresh per-question on-chain id + status best-effort (the page still renders if unreachable).
        try
        {
            var proc = await vocdoni.GetVotingProcessAsync(processId, ct);
            foreach (var q in p.Questions)
            {
                var h = proc.Questions.ElementAtOrDefault(q.Order);
                if (h is null) continue;
                if (!string.IsNullOrEmpty(h.UpstreamId)) q.UpstreamId = h.UpstreamId!;
                if (!string.IsNullOrEmpty(h.Status)) q.Status = h.Status!;
            }
        }
        catch (VocdoniApiException) { /* serve stored values */ }
        catch (HttpRequestException) { }

        var opts = vocdoniOptions.Value;
        var questions = p.Questions.OrderBy(q => q.Order).Select(q => new PublicQuestion(
            q.Id, q.Order, q.Title,
            JsonSerializer.Deserialize<List<string>>(q.ChoicesJson) ?? [],
            q.Kind, q.UpstreamId, q.Status)).ToList();

        return new VotingInfoResponse(
            p.VocdoniProcessId, opts.BaseUrl, opts.ChainId, p.Title, p.Description,
            p.StartDate, p.EndDate, p.Status.ToString(), questions);
    }
}
