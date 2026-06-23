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
[Route("api/associations/{associationId:int}/homeowners")]
public class HomeownersController(AppDbContext db, IVocdoniClient vocdoni) : ApiControllerBase
{
    /// <summary>List the association's homeowners (Vocdoni org members).</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<HomeownerResponse>>> List(int associationId, CancellationToken ct)
    {
        var (assoc, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var members = await vocdoni.ListMembersAsync(assoc!.VocdoniOrgAddress, ct);
        return members.Select(m => new HomeownerResponse(
            m.Id ?? "", m.Name ?? "", m.Surname, m.Email ?? "", m.MemberNumber, m.Weight)).ToList();
    }

    /// <summary>Add a homeowner to the association.</summary>
    [HttpPost]
    public async Task<ActionResult> Add(int associationId, AddHomeownerRequest req, CancellationToken ct)
    {
        var (assoc, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        var member = new VocdoniOrgMember
        {
            Name = req.Name,
            Surname = req.Surname,
            Email = req.Email,
            Phone = req.Phone,
            MemberNumber = req.MemberNumber,
            Weight = req.Weight,
        };
        var result = await vocdoni.AddMembersAsync(assoc!.VocdoniOrgAddress, [member], ct);
        return Ok(result);
    }

    /// <summary>Remove a homeowner by their Vocdoni member id.</summary>
    [HttpDelete("{memberId}")]
    public async Task<ActionResult> Delete(int associationId, string memberId, CancellationToken ct)
    {
        var (assoc, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;

        await vocdoni.DeleteMembersAsync(assoc!.VocdoniOrgAddress, [memberId], ct);
        return NoContent();
    }

    private async Task<(Association?, ActionResult?)> ResolveAsync(int associationId, CancellationToken ct)
    {
        var assoc = await db.Associations.FindAsync([associationId], ct);
        if (assoc is null) return (null, NotFound());
        if (!AssociationAccess.CanAccess(CurrentRole, CurrentUserId, assoc)) return (null, Forbid());
        return (assoc, null);
    }
}
