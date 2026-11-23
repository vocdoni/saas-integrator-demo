using HoaVoting.Api.Data;
using HoaVoting.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Tests;

public class MigrationTests
{
    // Migrate() (not EnsureCreated) runs the real migrations, and EF refuses to migrate a model that has
    // changes no migration captures — so this fails until the migration is generated.
    [Fact]
    public void Migrations_cover_the_model_and_store_the_publish_state()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var db = new AppDbContext(options))
        {
            db.Database.Migrate();
            var owner = new AppUser { Email = "o@test", Role = AppRole.Owner };
            db.Users.Add(owner);
            db.SaveChanges();
            var assoc = new Association { Name = "A", OwnerUserId = owner.Id, VocdoniOrgAddress = "0xaa" };
            db.Associations.Add(assoc);
            db.SaveChanges();
            db.Proposals.Add(new Proposal
            {
                AssociationId = assoc.Id, Title = "P", Status = ProposalStatus.Failed,
                PublishJobId = "job1", PublishError = "out of funds", RequirePassword = true,
            });
            db.SaveChanges();
        }

        using var read = new AppDbContext(options);
        var p = read.Proposals.Single();
        Assert.Equal((ProposalStatus.Failed, "job1", "out of funds", true), (p.Status, p.PublishJobId, p.PublishError, p.RequirePassword));
    }
}
