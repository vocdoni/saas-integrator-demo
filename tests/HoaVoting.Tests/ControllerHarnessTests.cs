using HoaVoting.Api.Controllers;
using HoaVoting.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace HoaVoting.Tests;

// Pins the harness itself against behaviour that exists today: tenancy via the caller's claims, and
// the read-side reconcile persisting what the process read reports.
public class ControllerHarnessTests : ControllerTestBase
{
    [Fact]
    public async Task Another_owner_is_forbidden()
    {
        var res = await As(new ProposalsController(Db, Vocdoni), userId: 4242, AppRole.Owner).List(Assoc.Id, default);

        Assert.IsType<ForbidResult>(res.Result);
        Assert.Empty(Vocdoni.Calls);
    }

    [Fact]
    public async Task List_refreshes_question_status_and_persists_closed()
    {
        var p = SeedProposal(questions: 2);
        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ended", "results");

        var res = await AsOwner(new ProposalsController(Db, Vocdoni)).List(Assoc.Id, default);

        Assert.Equal("Closed", Assert.Single(res.Value!).Status);
        using var db = NewDb();
        var saved = db.Proposals.Single(x => x.Id == p.Id);
        Assert.Equal(ProposalStatus.Closed, saved.Status);
        Assert.Equal(["ended", "results"], db.ProposalQuestions.OrderBy(q => q.Order).Select(q => q.Status));
    }
}
