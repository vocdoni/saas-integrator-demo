using System.Text.Json;
using HoaVoting.Api.Data;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoaVoting.Api.Controllers;

/// <summary>Public, read-only voting-page data keyed by the 24-hex ProcessID (#551). No auth.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/processes")]
public class VotingController(AppDbContext db, IVocdoniClient vocdoni, IOptions<VocdoniOptions> vocdoniOptions) : ControllerBase
{
    [HttpGet("{processId}")]
    public async Task<ActionResult<VotingInfoResponse>> Get(string processId, CancellationToken ct)
    {
        var p = await db.Proposals.SingleOrDefaultAsync(x => x.VocdoniProcessId == processId, ct);
        if (p is null) return NotFound();

        var choices = JsonSerializer.Deserialize<List<string>>(p.ChoicesJson) ?? [];

        // Live on-chain status/tally is best-effort — the page still renders if Vocdoni is unreachable.
        int? voteCount = null;
        string? onchainStatus = null;
        List<List<string>>? results = null;
        try
        {
            var r = await vocdoni.GetResultsAsync(processId, ct);
            voteCount = r.VoteCount;
            onchainStatus = r.Status;
            results = r.Results;
        }
        catch (VocdoniApiException) { /* leave nulls */ }
        catch (HttpRequestException) { /* leave nulls */ }

        // Census size (eligible voters) lives on the process detail — best-effort, like the tally.
        int? censusSize = null;
        try { censusSize = await vocdoni.GetCensusSizeAsync(processId, ct); }
        catch (VocdoniApiException) { /* leave null */ }
        catch (HttpRequestException) { /* leave null */ }

        return new VotingInfoResponse(
            p.VocdoniProcessId, p.VocdoniBundleId, vocdoniOptions.Value.BaseUrl, p.Title, p.Description, choices,
            p.StartDate, p.EndDate, p.Status.ToString(), voteCount, onchainStatus, results, censusSize, p.VotingType);
    }
}
