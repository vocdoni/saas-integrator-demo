using HoaVoting.Api.Dtos;
using HoaVoting.Api.Models;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HoaVoting.Api.Controllers;

/// <summary>The integrator behind every association (saas-backend #819): wallet balance + managed-org cap.</summary>
[Authorize(Roles = nameof(AppRole.SuperAdmin))]
[Route("api/integrator")]
public class IntegratorController(IVocdoniClient vocdoni) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IntegratorResponse>> Get(CancellationToken ct)
    {
        var info = await vocdoni.GetIntegratorInfoAsync(ct); // failure → 502 as usual

        // The wallet belongs to the integrator org, whose address is the managedBy of any managed org
        // (none yet → nothing has been paid for). A key without quota:read/managed:read just hides it.
        long? balance = null;
        string? currency = null;
        try
        {
            if (await vocdoni.GetIntegratorAddressAsync(ct) is { } address)
            {
                var wallet = await vocdoni.GetWalletAsync(address, ct);
                (balance, currency) = (wallet.BalanceCents, wallet.Currency);
            }
        }
        catch (Exception e) when (e is VocdoniApiException or HttpRequestException) { /* balance unknown */ }

        return new IntegratorResponse(info.CanCreateManagedOrg, info.ManagedOrgsCreated, balance, currency);
    }
}
