using System.Net;
using HoaVoting.Api.Controllers;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Models;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoaVoting.Tests;

// AC2 + AC6 + AC7 (RequirePassword on the wire): the resilient create, the shared reconcile, and the
// endpoints that resolve a proposal stuck publishing. The fake answers WaitForJobAsync directly.
public class ProposalLifecycleTests : ControllerTestBase
{
    public ProposalLifecycleTests()
    {
        Vocdoni.Members = [new() { Id = "m1" }, new() { Id = "m2" }];
    }

    private ProposalsController Proposals() => AsOwner(new ProposalsController(Db, Vocdoni));

    private VotingController Voting() => new(Db, Vocdoni, Options.Create(new VocdoniOptions { ServerUrl = "https://saas.test" }));

    private static CreateProposalRequest Request(int questions = 2, bool requirePassword = false, int days = 7) => new(
        "Budget", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(days),
        Enumerable.Range(0, questions).Select(i => new QuestionInput($"Q{i}", [new("Yes"), new("No")])).ToList(),
        RequirePassword: requirePassword);

    private static VocdoniApiException Refused(HttpStatusCode status, int code) =>
        new(status, $$"""{"error":"refused","code":{{code}}}""");

    private Proposal Saved(int id)
    {
        using var db = NewDb();
        return db.Proposals.Include(p => p.Questions).AsNoTracking().Single(p => p.Id == id);
    }

    // --- create --------------------------------------------------------------

    [Fact]
    public async Task Create_publish_refused_deletes_the_upstream_draft_and_the_row()
    {
        Vocdoni.OnPublish = _ => throw Refused(HttpStatusCode.PaymentRequired, 40175);

        var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => Proposals().Create(Assoc.Id, Request(), default));

        Assert.Equal(40175, ex.Code); // rethrown → Program.cs answers 502 with the readable detail
        Assert.Contains("DeleteProcess proc1", Vocdoni.Calls);
        using var db = NewDb();
        Assert.Empty(db.Proposals);
    }

    [Fact]
    public async Task Create_publish_refused_with_a_failing_delete_keeps_a_Failed_row()
    {
        Vocdoni.OnPublish = _ => throw Refused(HttpStatusCode.PaymentRequired, 40175);
        Vocdoni.OnDeleteProcess = _ => throw Refused(HttpStatusCode.Conflict, 40903);

        await Assert.ThrowsAsync<VocdoniApiException>(() => Proposals().Create(Assoc.Id, Request(), default));

        using var db = NewDb();
        var p = db.Proposals.Single();
        Assert.Equal(ProposalStatus.Failed, p.Status);
        Assert.StartsWith("The integrator's wallet doesn't cover this", p.PublishError);
    }

    [Fact]
    public async Task Create_failed_job_keeps_a_Failed_row_and_answers_502()
    {
        Vocdoni.OnWaitForJob = _ => (JobState.Failed, "chain rejected the batch");

        var res = await Proposals().Create(Assoc.Id, Request(), default);

        Assert.Equal(502, StatusOf(res));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("DeleteProcess")); // never auto-delete after a job
        using var db = NewDb();
        var p = db.Proposals.Single();
        Assert.Equal((ProposalStatus.Failed, "chain rejected the batch", "job1"), (p.Status, p.PublishError, p.PublishJobId));
    }

    [Fact]
    public async Task Create_pending_job_answers_202_with_a_Draft()
    {
        Vocdoni.OnWaitForJob = _ => (JobState.Pending, null);
        Vocdoni.OnCreateProcess = _ => new() { ProcessId = "proc1", MissingData = ["m2"] };

        var res = await Proposals().Create(Assoc.Id, Request(), default);

        Assert.Equal(202, StatusOf(res));
        var body = ValueOf(res)!;
        Assert.Equal("Draft", body.Status);
        Assert.Equal(["m2"], body.MissingData!);
        Assert.Equal("job1", Saved(body.Id).PublishJobId);
    }

    [Fact]
    public async Task Create_completed_job_answers_201_Open_with_upstream_ids()
    {
        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ready", "ready");

        var res = await Proposals().Create(Assoc.Id, Request(), default);

        Assert.Equal(201, StatusOf(res));
        var p = Saved(ValueOf(res)!.Id);
        Assert.Equal((ProposalStatus.Open, "vocdoni-test"), (p.Status, p.ChainId));
        Assert.Equal(["e0", "e1"], p.Questions.OrderBy(q => q.Order).Select(q => q.UpstreamId));
    }

    [Fact]
    public async Task Create_whose_job_wait_times_out_answers_202_with_a_Draft()
    {
        Vocdoni.OnWaitForJob = _ => throw new TaskCanceledException("HttpClient.Timeout elapsed");

        var res = await Proposals().Create(Assoc.Id, Request(), default);

        Assert.Equal(202, StatusOf(res));
        var p = Saved(ValueOf(res)!.Id);
        Assert.Equal((ProposalStatus.Draft, "job1"), (p.Status, p.PublishJobId));
    }

    [Fact]
    public async Task Create_already_published_skips_the_job()
    {
        Vocdoni.OnPublish = _ => null; // 200: already published
        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ready", "ready");

        var res = await Proposals().Create(Assoc.Id, Request(), default);

        Assert.Equal(201, StatusOf(res));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("WaitForJob"));
    }

    [Fact]
    public async Task Create_completed_job_whose_read_back_throws_answers_202_with_a_Draft()
    {
        Vocdoni.OnGetProcess = _ => throw new HttpRequestException("connection reset");

        var res = await Proposals().Create(Assoc.Id, Request(), default);

        Assert.Equal(202, StatusOf(res)); // charged publish: never 502; the reconcile promotes it
        var p = Saved(ValueOf(res)!.Id);
        Assert.Equal((ProposalStatus.Draft, "job1"), (p.Status, p.PublishJobId));
    }

    [Fact]
    public async Task Create_completed_job_whose_read_back_lags_answers_202_with_a_Draft()
    {
        Vocdoni.OnGetProcess = id =>
        {
            var proc = PublishedProcess(id, "ready", "ready");
            proc.Questions[1].UpstreamId = null; // second election not visible yet
            return proc;
        };

        var res = await Proposals().Create(Assoc.Id, Request(), default);

        Assert.Equal(202, StatusOf(res));
        var p = Saved(ValueOf(res)!.Id);
        Assert.Equal(ProposalStatus.Draft, p.Status);
        Assert.All(p.Questions, q => Assert.Equal("", q.UpstreamId));
    }

    [Fact]
    public async Task Create_with_no_answer_from_publish_keeps_a_Draft_without_job()
    {
        Vocdoni.OnPublish = _ => throw new HttpRequestException("connection reset");

        await Assert.ThrowsAsync<HttpRequestException>(() => Proposals().Create(Assoc.Id, Request(), default));

        using var db = NewDb();
        var p = db.Proposals.Single();
        Assert.Equal((ProposalStatus.Draft, null), (p.Status, p.PublishJobId));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("DeleteProcess"));
    }

    [Fact]
    public async Task Create_rejects_over_15_questions_and_over_90_days_before_any_upstream_call()
    {
        Assert.Equal(400, StatusOf(await Proposals().Create(Assoc.Id, Request(questions: 16), default)));
        Assert.Equal(400, StatusOf(await Proposals().Create(Assoc.Id, Request(days: 91), default)));
        Assert.Empty(Vocdoni.Calls);
    }

    [Fact]
    public async Task RequirePassword_sends_the_password_auth_field_and_is_stored()
    {
        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ready", "ready");

        var res = await Proposals().Create(Assoc.Id, Request(requirePassword: true), default);

        Assert.Equal(["memberNumber", "password"], Vocdoni.LastCreate!.Census.AuthFields!);
        Assert.True(Saved(ValueOf(res)!.Id).RequirePassword);

        await Proposals().Create(Assoc.Id, Request(), default);
        Assert.Equal(["memberNumber"], Vocdoni.LastCreate!.Census.AuthFields!);
    }

    // --- reconcile -------------------------------------------------------------

    [Fact]
    public async Task Read_promotes_a_published_Draft_to_Open()
    {
        var p = SeedProposal(ProposalStatus.Draft, questions: 2, published: false);
        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ready", "ready");

        var res = await Proposals().Get(Assoc.Id, p.Id, default);

        Assert.Equal("Open", res.Value!.Status);
        var saved = Saved(p.Id);
        Assert.Equal(ProposalStatus.Open, saved.Status);
        Assert.Equal(["e0", "e1"], saved.Questions.OrderBy(q => q.Order).Select(q => q.UpstreamId));
    }

    [Fact]
    public async Task Read_keeps_a_Draft_until_every_question_has_an_upstream_id()
    {
        var p = SeedProposal(ProposalStatus.Draft, questions: 2, published: false);
        Vocdoni.OnGetProcess = id =>
        {
            var proc = PublishedProcess(id, "ready", "ready");
            proc.Questions[1].UpstreamId = null; // second election not mined yet
            return proc;
        };

        await Proposals().Get(Assoc.Id, p.Id, default);

        Assert.Equal(ProposalStatus.Draft, Saved(p.Id).Status);
    }

    [Fact]
    public async Task Read_marks_a_Draft_Failed_when_its_job_failed()
    {
        var p = SeedProposal(ProposalStatus.Draft, published: false);
        p.PublishJobId = "job7";
        Db.SaveChanges();
        Vocdoni.OnGetProcess = id => new() { Id = id, Published = false };
        Vocdoni.OnGetJob = _ => new() { Status = "failed", Errors = ["out of funds", "retry later"] };

        await Proposals().List(Assoc.Id, default);

        var saved = Saved(p.Id);
        Assert.Equal((ProposalStatus.Failed, "out of funds; retry later"), (saved.Status, saved.PublishError));
    }

    [Fact]
    public async Task Read_fails_a_Draft_with_a_fallback_text_when_the_failed_job_has_no_errors()
    {
        var p = SeedProposal(ProposalStatus.Draft, published: false);
        p.PublishJobId = "job7";
        Db.SaveChanges();
        Vocdoni.OnGetJob = _ => new() { Status = "failed" };

        await Proposals().Get(Assoc.Id, p.Id, default);

        var saved = Saved(p.Id);
        Assert.Equal((ProposalStatus.Failed, "The publish job failed."), (saved.Status, saved.PublishError));
    }

    [Fact]
    public async Task Failed_and_Draft_past_their_end_date_are_never_closed()
    {
        var failed = SeedProposal(ProposalStatus.Failed, published: false, endDate: DateTimeOffset.UtcNow.AddDays(-1));
        var draft = SeedProposal(ProposalStatus.Draft, published: false, endDate: DateTimeOffset.UtcNow.AddDays(-1), processId: "proc2");
        Vocdoni.OnGetProcess = _ => throw new VocdoniApiException(HttpStatusCode.NotFound, "{}");

        await Proposals().List(Assoc.Id, default);

        Assert.Equal(ProposalStatus.Failed, Saved(failed.Id).Status);
        Assert.Equal(ProposalStatus.Draft, Saved(draft.Id).Status);
    }

    [Fact]
    public async Task Read_copies_the_upstream_end_date()
    {
        var p = SeedProposal();
        var later = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Vocdoni.OnGetProcess = id =>
        {
            var proc = PublishedProcess(id, "ready");
            proc.EndDate = "2030-01-02T03:04:05Z";
            return proc;
        };

        await Proposals().Get(Assoc.Id, p.Id, default);

        Assert.Equal(later, Saved(p.Id).EndDate);
    }

    [Fact]
    public async Task Read_survives_the_row_being_deleted_meanwhile()
    {
        var p = SeedProposal(ProposalStatus.Draft, published: false);
        Vocdoni.OnGetProcess = id =>
        {
            using (var other = NewDb()) // a concurrent DELETE lands between our read and our save
            {
                other.Proposals.Remove(other.Proposals.Single(x => x.Id == p.Id));
                other.SaveChanges();
            }
            return PublishedProcess(id, "ready");
        };

        var res = await Proposals().List(Assoc.Id, default);

        Assert.Equal("Open", Assert.Single(res.Value!).Status); // served from what it read
    }

    [Fact]
    public async Task Public_page_promotes_a_published_Draft_and_404s_until_then()
    {
        var p = SeedProposal(ProposalStatus.Draft, published: false);
        Vocdoni.OnGetProcess = id => new() { Id = id, Published = false };

        Assert.Equal(404, StatusOf(await Voting().Get("proc1", default)));

        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ready");
        var res = await Voting().Get("proc1", default);

        Assert.Equal("Open", res.Value!.Status);
        Assert.Equal(Org, res.Value.OrgAddress);
        Assert.Equal("e0", Assert.Single(res.Value.Questions).UpstreamId);
        Assert.Equal(ProposalStatus.Open, Saved(p.Id).Status);
    }

    [Fact]
    public async Task Public_page_404s_a_Failed_proposal()
    {
        SeedProposal(ProposalStatus.Failed, published: false);

        Assert.Equal(404, StatusOf(await Voting().Get("proc1", default)));
    }

    // --- delete / retry / close ------------------------------------------------

    [Fact]
    public async Task Delete_refuses_an_Open_proposal()
    {
        var p = SeedProposal(ProposalStatus.Open);

        Assert.Equal(409, StatusOf(await Proposals().Delete(Assoc.Id, p.Id, default)));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("DeleteProcess"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Delete_removes_the_row_when_upstream_deleted_or_never_had_it(HttpStatusCode upstream)
    {
        var p = SeedProposal(ProposalStatus.Failed, published: false);
        if (upstream != HttpStatusCode.OK)
            Vocdoni.OnDeleteProcess = _ => throw new VocdoniApiException(upstream, "{}");

        Assert.Equal(204, StatusOf(await Proposals().Delete(Assoc.Id, p.Id, default)));
        using var db = NewDb();
        Assert.Empty(db.Proposals);
    }

    [Fact]
    public async Task Delete_keeps_the_row_when_upstream_refuses()
    {
        var p = SeedProposal(ProposalStatus.Draft, published: false);
        Vocdoni.OnDeleteProcess = _ => throw Refused(HttpStatusCode.Conflict, 40904);

        // Propagates → Program.cs answers 502.
        await Assert.ThrowsAsync<VocdoniApiException>(() => Proposals().Delete(Assoc.Id, p.Id, default));
        var saved = Saved(p.Id);
        Assert.Equal(ProposalStatus.Failed, saved.Status); // Retry publish is now offered
        Assert.Equal("refused", saved.PublishError);

        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ready");
        Assert.Equal(200, StatusOf(await Proposals().Publish(Assoc.Id, p.Id, default)));
        Assert.Equal(ProposalStatus.Open, Saved(p.Id).Status);
    }

    [Fact]
    public async Task Delete_of_a_Failed_proposal_refused_by_upstream_stays_Failed()
    {
        var p = SeedProposal(ProposalStatus.Failed, published: false);
        p.PublishError = "out of funds";
        Db.SaveChanges();
        Vocdoni.OnDeleteProcess = _ => throw Refused(HttpStatusCode.Conflict, 40903);

        await Assert.ThrowsAsync<VocdoniApiException>(() => Proposals().Delete(Assoc.Id, p.Id, default));
        var saved = Saved(p.Id);
        Assert.Equal((ProposalStatus.Failed, "out of funds"), (saved.Status, saved.PublishError));
    }

    [Fact]
    public async Task Retry_of_a_Failed_proposal_whose_job_completes_makes_it_Open()
    {
        var p = SeedProposal(ProposalStatus.Failed, questions: 2, published: false);
        p.PublishError = "out of funds";
        Db.SaveChanges();
        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ready", "ready");

        var res = await Proposals().Publish(Assoc.Id, p.Id, default);

        Assert.Equal(200, StatusOf(res));
        var saved = Saved(p.Id);
        Assert.Equal((ProposalStatus.Open, null), (saved.Status, saved.PublishError));
    }

    [Fact]
    public async Task Retry_refused_again_stays_Failed_and_deletes_nothing()
    {
        var p = SeedProposal(ProposalStatus.Failed, published: false);
        Vocdoni.OnPublish = _ => throw Refused(HttpStatusCode.PaymentRequired, 40183);

        await Assert.ThrowsAsync<VocdoniApiException>(() => Proposals().Publish(Assoc.Id, p.Id, default));

        var saved = Saved(p.Id);
        Assert.Equal((ProposalStatus.Failed, "This association reached the spending cap set by its integrator."), (saved.Status, saved.PublishError));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("DeleteProcess"));
    }

    [Fact]
    public async Task Retry_refuses_a_proposal_that_did_not_fail()
    {
        var p = SeedProposal(ProposalStatus.Open);

        Assert.Equal(409, StatusOf(await Proposals().Publish(Assoc.Id, p.Id, default)));
    }

    [Theory]
    [InlineData(ProposalStatus.Draft)]
    [InlineData(ProposalStatus.Failed)]
    [InlineData(ProposalStatus.Closed)]
    public async Task Close_refuses_a_proposal_that_is_not_Open(ProposalStatus status)
    {
        var p = SeedProposal(status);

        Assert.Equal(409, StatusOf(await Proposals().Close(Assoc.Id, p.Id, default)));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("SetQuestionsStatus"));
    }

    // --- end date (AC6) ----------------------------------------------------------

    [Fact]
    public async Task EndDate_saves_the_new_end_once_the_job_completed()
    {
        var p = SeedProposal();
        var newEnd = p.EndDate.AddDays(3);

        var res = await Proposals().SetEndDate(Assoc.Id, p.Id, new(newEnd), default);

        Assert.Equal(200, StatusOf(res));
        Assert.Equal(newEnd, Vocdoni.LastEndDate);
        Assert.Equal(newEnd, Saved(p.Id).EndDate);
    }

    [Fact]
    public async Task EndDate_rejects_a_date_that_is_not_later_or_a_proposal_that_is_not_Open()
    {
        var open = SeedProposal();
        var draft = SeedProposal(ProposalStatus.Draft, published: false, processId: "proc2");

        Assert.Equal(400, StatusOf(await Proposals().SetEndDate(Assoc.Id, open.Id, new(open.EndDate), default)));
        Assert.Equal(400, StatusOf(await Proposals().SetEndDate(Assoc.Id, draft.Id, new(draft.EndDate.AddDays(1)), default)));
        Assert.Empty(Vocdoni.Calls);
    }

    [Fact]
    public async Task EndDate_pending_answers_202_and_failed_answers_502_leaving_the_end_unchanged()
    {
        var p = SeedProposal();
        var oldEnd = p.EndDate;

        Vocdoni.OnWaitForJob = _ => (JobState.Pending, null);
        Assert.Equal(202, StatusOf(await Proposals().SetEndDate(Assoc.Id, p.Id, new(oldEnd.AddDays(1)), default)));
        Assert.Equal(oldEnd, Saved(p.Id).EndDate);

        Vocdoni.OnWaitForJob = _ => (JobState.Failed, "1 of 1 questions failed");
        Assert.Equal(502, StatusOf(await Proposals().SetEndDate(Assoc.Id, p.Id, new(oldEnd.AddDays(1)), default)));
        Assert.Equal(oldEnd, Saved(p.Id).EndDate);
    }
}
