using HoaVoting.Api.Data;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Models;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Api.Controllers;

[Authorize]
[Route("api/associations")]
public class AssociationsController(AppDbContext db, IVocdoniClient vocdoni) : ApiControllerBase
{
    private static readonly PasswordHasher<AppUser> Hasher = new();

    /// <summary>Admin creates an association: a Vocdoni org + an Owner app user.</summary>
    [Authorize(Roles = nameof(AppRole.SuperAdmin))]
    [HttpPost]
    public async Task<ActionResult<AssociationResponse>> Create(CreateAssociationRequest req, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Email == req.OwnerEmail, ct))
            return Conflict($"A user with email {req.OwnerEmail} already exists.");

        var orgAddress = await vocdoni.CreateOrganizationAsync(req.Name, ct);

        var owner = new AppUser { Email = req.OwnerEmail, Role = AppRole.Owner, CreatedAt = DateTimeOffset.UtcNow };
        owner.PasswordHash = Hasher.HashPassword(owner, req.OwnerPassword);
        db.Users.Add(owner);
        await db.SaveChangesAsync(ct);

        var assoc = new Association
        {
            Name = req.Name,
            OwnerUserId = owner.Id,
            VocdoniOrgAddress = orgAddress,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Associations.Add(assoc);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = assoc.Id },
            new AssociationResponse(assoc.Id, assoc.Name, owner.Email, assoc.VocdoniOrgAddress, assoc.CreatedAt));
    }

    /// <summary>
    /// Adopt an EXISTING Vocdoni managed org as an association (no Vocdoni create call). Useful when
    /// the integrator's managed-org quota is full but an org already exists to manage.
    /// </summary>
    [Authorize(Roles = nameof(AppRole.SuperAdmin))]
    [HttpPost("import")]
    public async Task<ActionResult<AssociationResponse>> Import(ImportAssociationRequest req, CancellationToken ct)
    {
        if (await db.Associations.AnyAsync(a => a.VocdoniOrgAddress == req.VocdoniOrgAddress, ct))
            return Conflict("An association is already registered for this org address.");
        if (await db.Users.AnyAsync(u => u.Email == req.OwnerEmail, ct))
            return Conflict($"A user with email {req.OwnerEmail} already exists.");

        var owner = new AppUser { Email = req.OwnerEmail, Role = AppRole.Owner, CreatedAt = DateTimeOffset.UtcNow };
        owner.PasswordHash = Hasher.HashPassword(owner, req.OwnerPassword);
        db.Users.Add(owner);
        await db.SaveChangesAsync(ct);

        var assoc = new Association
        {
            Name = req.Name,
            OwnerUserId = owner.Id,
            VocdoniOrgAddress = req.VocdoniOrgAddress,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Associations.Add(assoc);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = assoc.Id },
            new AssociationResponse(assoc.Id, assoc.Name, owner.Email, assoc.VocdoniOrgAddress, assoc.CreatedAt));
    }

    [Authorize(Roles = nameof(AppRole.SuperAdmin))]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AssociationResponse>>> List(CancellationToken ct)
    {
        var items = await db.Associations.Include(a => a.Owner).OrderBy(a => a.Id).ToListAsync(ct);
        return items.Select(a => new AssociationResponse(
            a.Id, a.Name, a.Owner!.Email, a.VocdoniOrgAddress, a.CreatedAt)).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AssociationResponse>> Get(int id, CancellationToken ct)
    {
        var assoc = await db.Associations.Include(a => a.Owner).SingleOrDefaultAsync(a => a.Id == id, ct);
        if (assoc is null) return NotFound();
        if (!Authorization.AssociationAccess.CanAccess(CurrentRole, CurrentUserId, assoc)) return Forbid();

        return new AssociationResponse(assoc.Id, assoc.Name, assoc.Owner!.Email, assoc.VocdoniOrgAddress, assoc.CreatedAt);
    }
}
