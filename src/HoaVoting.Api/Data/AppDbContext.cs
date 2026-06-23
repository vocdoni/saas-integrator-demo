using HoaVoting.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Association> Associations => Set<Association>();
    public DbSet<Proposal> Proposals => Set<Proposal>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();

        b.Entity<Association>()
            .HasOne(a => a.Owner)
            .WithMany()
            .HasForeignKey(a => a.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<Proposal>()
            .HasOne(p => p.Association)
            .WithMany(a => a.Proposals)
            .HasForeignKey(p => p.AssociationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
