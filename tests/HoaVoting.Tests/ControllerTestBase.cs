using System.Security.Claims;
using HoaVoting.Api.Data;
using HoaVoting.Api.Models;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Tests;

/// <summary>
/// Controller-test harness: one SQLite in-memory database per test (it lives as long as the open
/// connection), seeded with an Owner and their association, plus a <see cref="FakeVocdoniClient"/>.
/// Controllers are constructed directly — no hosting, no middleware — so an upstream failure shows up
/// as the thrown <see cref="VocdoniApiException"/> that Program.cs maps to 502.
/// Arrange/act use <see cref="Db"/>; assert on persisted state through a fresh <see cref="NewDb"/>.
/// </summary>
public abstract class ControllerTestBase : IDisposable
{
    protected const string Org = "0x00000000000000000000000000000000000000aa";

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    protected readonly AppDbContext Db;
    protected readonly FakeVocdoniClient Vocdoni = new();
    protected readonly Association Assoc;

    protected ControllerTestBase()
    {
        _conn.Open();
        Db = NewDb();
        Db.Database.EnsureCreated();
        var owner = new AppUser { Email = "owner@test", Role = AppRole.Owner };
        Db.Users.Add(owner);
        Db.SaveChanges();
        Assoc = new Association { Name = "HOA", OwnerUserId = owner.Id, VocdoniOrgAddress = Org };
        Db.Associations.Add(Assoc);
        Db.SaveChanges();
    }

    protected AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);

    /// <summary>Gives the controller a caller carrying the claims ApiControllerBase reads (sub + role).</summary>
    protected static T As<T>(T controller, int userId, AppRole role) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("sub", userId.ToString()), new Claim(ClaimTypes.Role, role.ToString())], "test")),
            },
        };
        return controller;
    }

    protected T AsOwner<T>(T controller) where T : ControllerBase => As(controller, Assoc.OwnerUserId, AppRole.Owner);
    protected static T AsAdmin<T>(T controller) where T : ControllerBase => As(controller, 999, AppRole.SuperAdmin);

    /// <summary>A proposal with <paramref name="questions"/> questions; published ones carry upstream ids e0, e1, ….</summary>
    protected Proposal SeedProposal(
        ProposalStatus status = ProposalStatus.Open, int questions = 1, bool published = true,
        DateTimeOffset? endDate = null, string processId = "proc1")
    {
        var p = new Proposal
        {
            AssociationId = Assoc.Id,
            Title = "P",
            VocdoniProcessId = processId,
            Status = status,
            StartDate = DateTimeOffset.UtcNow.AddHours(-1),
            EndDate = endDate ?? DateTimeOffset.UtcNow.AddDays(1),
            Questions = Enumerable.Range(0, questions).Select(i => new ProposalQuestion
            {
                Order = i,
                Title = $"Q{i}",
                ChoicesJson = """["Yes","No"]""",
                UpstreamId = published ? $"e{i}" : "",
                Status = published ? "ready" : "",
            }).ToList(),
        };
        Db.Proposals.Add(p);
        Db.SaveChanges();
        return p;
    }

    /// <summary>A process read: published, one question per status with upstream ids e0, e1, ….</summary>
    protected static VotingProcessResponse PublishedProcess(string id, params string[] statuses) => new()
    {
        Id = id,
        Published = true,
        ChainId = "vocdoni-test",
        Questions = statuses.Select((s, i) => new VotingProcessQuestion { Id = $"q{i}", UpstreamId = $"e{i}", Status = s }).ToList(),
    };

    protected static int? StatusOf(IActionResult? r) => (r as IStatusCodeActionResult)?.StatusCode;
    protected static int? StatusOf<T>(ActionResult<T> r) => r.Result is null ? 200 : StatusOf(r.Result);
    protected static T? ValueOf<T>(ActionResult<T> r) where T : class => r.Value ?? (r.Result as ObjectResult)?.Value as T;

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
