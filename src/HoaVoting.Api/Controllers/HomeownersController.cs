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
            m.Id ?? "", m.Name ?? "", m.Surname, m.Email ?? "", m.MemberNumber, m.Weight, m.HasPassword)).ToList();
    }

    /// <summary>Add a homeowner to the association.</summary>
    [HttpPost]
    public async Task<ActionResult> Add(int associationId, AddHomeownerRequest req, CancellationToken ct)
    {
        var (assoc, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;
        if (req.PasswordHash is not null && !IsPasswordHash(req.PasswordHash)) return BadRequest(PasswordHashRule);

        var member = new VocdoniOrgMember
        {
            Name = req.Name,
            Surname = req.Surname,
            Email = req.Email,
            Phone = req.Phone,
            MemberNumber = req.MemberNumber,
            Weight = req.Weight,
            Password = req.PasswordHash,
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

    /// <summary>
    /// Set a homeowner's password (saas-backend #760) from its browser-computed hash. The member must
    /// exist: upstream's upsert treats an unknown id as a create, which would add a phantom member to
    /// live censuses (and can charge the integrator for the growth).
    /// </summary>
    [HttpPut("{memberId}/password")]
    public async Task<ActionResult> SetPassword(int associationId, string memberId, SetPasswordRequest req, CancellationToken ct)
    {
        var (assoc, error) = await ResolveAsync(associationId, ct);
        if (error is not null) return error;
        if (!IsPasswordHash(req.PasswordHash)) return BadRequest(PasswordHashRule);

        var members = await vocdoni.ListMembersAsync(assoc!.VocdoniOrgAddress, ct);
        if (!members.Any(m => m.Id == memberId)) return NotFound();

        await vocdoni.UpsertMemberAsync(assoc.VocdoniOrgAddress, new VocdoniOrgMember { Id = memberId, Password = req.PasswordHash }, ct);
        return NoContent();
    }

    private const string PasswordHashRule = "passwordHash must be the 64-character lowercase hex client hash (hashMemberPassword), never the password.";

    // Trust boundary: only the client hash may pass. Char check, not a regex: "$" would accept a trailing newline.
    internal static bool IsPasswordHash(string? s) => s is { Length: 64 } && s.All(char.IsAsciiHexDigitLower);

    private async Task<(Association?, ActionResult?)> ResolveAsync(int associationId, CancellationToken ct)
    {
        var assoc = await db.Associations.FindAsync([associationId], ct);
        if (assoc is null) return (null, NotFound());
        if (!AssociationAccess.CanAccess(CurrentRole, CurrentUserId, assoc)) return (null, Forbid());
        return (assoc, null);
    }
}
