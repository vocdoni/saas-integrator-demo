# saas-backend sync (Sep → Nov 2026): implementation plan

Spec: docs/specs/2026-11-20-saas-backend-sync-design.md (approved 3543f6e)
Class: architectural
Repos and branches: /home/lucasmenendez/saas-integrator-demo/.worktrees/feature-saas-demo → feature/saas-demo (base 3543f6eeea4a0235d7126e0af652f60e424cd3c0). All work stays on this branch; no new branch.

How to read the code blocks: every block was applied and run in a scratch clone (see "Verified" at the end
of this header). A ```` ```diff ```` block is a `git apply`-able patch against the state left by the previous
task (save it to a file under `~/.cache/saas-demo/` and `git apply` it from the repo root, or edit by
hand). "new file" blocks are complete files. Line numbers cited as `file:line` refer to the base commit.

Plan decisions the spec leaves open (binding for implementers):
- `VocdoniApiException` gains `Code`, `UpstreamError` (the body's `error` text) and `ErrorData` — not
  `Data`, which would hide `Exception.Data` (compiler warning CS0114).
- The publish routine and the read-side reconcile live in one static class, `Services/ProposalSync.cs`
  (`PublishAsync`, `ReconcileAsync`, `SaveAsync`), shared by `ProposalsController` and
  `VotingController`; no interface, no DI registration.
- The `managedBy` lookup is `IVocdoniClient.GetIntegratorAddressAsync()` (`GET
  /integrator/organizations?page=1&limit=1`); `GET api/integrator` is a new `IntegratorController`.
- `WaitForJobAsync` polls 20 times with `VocdoniClient.PollDelay` (2 s, `internal init`, tests set 0).
  In the publish routine a job *read* that throws while waiting counts as Pending (202): the job id is
  already stored and the reconcile follows it.
- Retry publish clears `PublishJobId` too, so the reconcile cannot fail the retry from the old job.
- Password hashes are validated with `s is { Length: 64 } && s.All(char.IsAsciiHexDigitLower)` — not a
  regex, since `$` accepts a trailing newline.

Verified in a scratch clone (`~/.cache/saas-demo/scratch/repo`, never the worktree), task by task in
this order: every task's failing step fails as stated, then `dotnet test` (sdk:10.0 container) reaches
the stated count (31 → 33 → 42 → 43 → 73 → 85), `npm test` 15 → 18, `npm run build` green after Tasks
2, 7, 8 and 9, the migration was generated with the container recipe, AC1's pattern matches nothing,
`bash -n e2e.sh` passes. The plan itself was then replayed on a fresh clone of the base by a script
that extracts these blocks (`git apply` for diffs) and runs each step: same outputs, and the final tree
is identical to the scratch implementation (migration timestamps aside). Not
verified: the SPA in a browser (no stack was run), `./e2e.sh` and every live upstream call (no `.env`).

## Global Constraints
- Commits are conventional `type(scope): lowercase summary`, created with `git -c commit.gpgsign=false commit ... -- <explicit paths>` (never signed, never `git add -A`), no AI attribution and no Co-authored-by trailers, never push; subagents never dispatch subagents.
- New files must be staged first (`git add <path>`; deletions with `git rm <path>`) so `git commit -- <paths>` sees them; stage only the task's listed paths.
- Never sign anything: any commit, merge, cherry-pick, revert or rebase passes `-c commit.gpgsign=false`; never change git config to avoid signing.
- New Vocdoni calls go in `IVocdoniClient` + `VocdoniClient`, never inline in a controller.
- Upstream failures stay 502 (`VocdoniApiException` → `Program.cs`), no retries.
- Controllers stay thin; ownership via `AssociationAccess`.
- Hex addresses on the wire. `ponytail:` comments mark deliberate shortcuts.
- No local .NET SDK: build/test/migrate in `mcr.microsoft.com/dotnet/sdk:10.0` as CLAUDE.md shows. Tests: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test` from the repo root (optionally add `-v ~/.cache/saas-demo/nuget:/root/.nuget/packages` to cache packages). The container writes root-owned `bin/`/`obj/` (gitignored) and, in Task 4, root-owned migration files; commit them as they are, do not chown the repo.
- The EF migration (Task 4) is generated with CLAUDE.md's container recipe, never hand-written or hand-edited.
- npm runs locally in `web/` (node v24 is installed): `npm test`, `npm run build`, `npm install`.
- Scratch files, saved patches and build output go under `~/.cache/saas-demo/`, never `/tmp`, never inside the repo.
- Do not touch `.delivery/`, `docs/specs/`, `docs/plans/`, `.env`, or git config.
- Behaviour that must stay unchanged: the 502 status for upstream failures; `SetQuestionCensusAsync` still throws on a failed or timed-out job; Close stays fire-and-forget (202, no poll); manager-only memos never reach `VotingController`; the 40173 eligibility-conflict parse; the existing 29 xUnit and 15 vitest tests keep passing except the ones this plan replaces (4 subscription tests deleted in Task 2, 3 publish-poll tests rewritten in Task 3).

## Review Focus
- Publish routine state machine: `src/HoaVoting.Api/Services/ProposalSync.cs` `PublishAsync` (new; replaces `ProposalsController.cs:59-90`). Refused publish → upstream delete → row removed, or `Failed` when the delete fails; no answer → `Draft` without job id, rethrown; 202 → job id saved before waiting; failed job → `Failed`, never auto-deleted; 200/completed → read-back → `Open`. Retry passes `deleteOnRefusal: false`.
- `CancellationToken.None` on every save after an upstream side effect: `ProposalSync.cs` (the four saves in `PublishAsync`, `SaveAsync`, the cleanup delete in `TryDeleteUpstreamAsync`) and `ProposalsController` (`Create` row save before publish, `Delete`, retry's pre-publish save, `SetEndDate`).
- Reconcile ordering: `ProposalSync.ReconcileAsync` — 1 settle `Draft` (promote only on `published` + every `upstreamId` by order; else fail from the stored job), 2 copy `endDate`, 3 question status, 4 Closed rule for `Open` only (replaces `ProposalsController.cs:218-250`, whose rule closed any non-Closed row). Never deletes; `SaveAsync` swallows `DbUpdateConcurrencyException` only. `VotingController.Get` (`VotingController.cs:21-57`) now saves and 404s `Draft`/`Failed`.
- Password hash trust boundary: `HomeownersController.IsPasswordHash` on Add and SetPassword (64 lowercase hex, char check); the plaintext only exists in `web/src/voting.js` (`hashMemberPassword` before `elections.authStep0`) and `web/src/components/Memberbase.jsx`; nothing logs request bodies.
- Phantom-member check: `HomeownersController.SetPassword` lists members and 404s unless `memberId` is one of them, before `UpsertMemberAsync` (upstream upsert creates a member for an unknown id).
- 502 mapping: `Program.cs:61-74` keeps status 502 and now writes `{error, code, detail}` from `UpstreamErrors.Describe`; controller-built 502s (`ProposalsController.UpstreamFailure`) use the same `{error, detail}` shape.

## Task order and parallelism
| Task | Depends on | Repo/files | May run in parallel with |
|---|---|---|---|
| 1 Test harness | — | tests/HoaVoting.Tests/{FakeVocdoniClient,ControllerTestBase,ControllerHarnessTests}.cs | 4, 7 (disjoint files) |
| 2 Plan gating out, readable 502 | 1 | Services/Vocdoni/{VocdoniClient,IVocdoniClient,VocdoniModels,UpstreamErrors}.cs, Program.cs, AssociationsController.cs, Dtos.cs, tests (Fake, VocdoniClientTests, UpstreamErrorsTests), web/src/components/Proposals.jsx, docker-compose.yml | 4, 7 |
| 3 VocdoniClient calls | 2 | Services/Vocdoni/{VocdoniClient,IVocdoniClient,VocdoniModels}.cs, ProposalsController.cs (interim lines), tests (Fake, VocdoniClientTests) | 4, 7 |
| 4 EF model + migration | — | Models/Proposal.cs, Migrations/*, tests/MigrationTests.cs | 1, 2, 3, 7 |
| 5 Resilient create + reconcile + endpoints | 3, 4 | Services/ProposalSync.cs, ProposalsController.cs, VotingController.cs, Dtos.cs, tests/ProposalLifecycleTests.cs | 7 |
| 6 Price, integrator, homeowner password | 5 (Dtos.cs) | Dtos.cs, AssociationsController.cs, HomeownersController.cs, IntegratorController.cs, tests/BillingAndPasswordTests.cs | 7 |
| 7 SDK bump + voting.js | — | web/package.json, web/package-lock.json, web/src/voting.js, web/src/voting.test.js | 1–6 |
| 8 SPA | 2, 5, 6, 7 | web/src/components/{Proposals,Memberbase,AdminPage,VotingPage}.jsx, web/src/styles.css | — |
| 9 Scripts + docs | 1–8 | e2e.sh, create-process.sh (deleted), README.md, CLAUDE.md, requests.http, .env.example | — |

Parallel only in a separate worktree: there is one checkout of `feature/saas-demo`, and concurrent
`dotnet test` containers share `bin/`/`obj/`. If the controller parallelizes (4 or 7 next to 1–3), the
parallel task works in its own worktree on a temporary branch from the same base and its commit is
cherry-picked onto `feature/saas-demo` with `git -c commit.gpgsign=false cherry-pick`. Default: run
the tasks sequentially in the order above. Task 4 regenerates nothing that 2–3 touch, but its migration
build compiles the API, so it must not run while 2/3 have the API mid-edit in the same checkout.

## Tasks

### Task 1: Controller test harness
Model: standard   Repo: /home/lucasmenendez/saas-integrator-demo/.worktrees/feature-saas-demo   Files: tests/HoaVoting.Tests/FakeVocdoniClient.cs, tests/HoaVoting.Tests/ControllerTestBase.cs, tests/HoaVoting.Tests/ControllerHarnessTests.cs
Goal: the spec's Test approach — a scripted `FakeVocdoniClient` and SQLite in-memory controller tests with the `sub` + role claims `ApiControllerBase.cs:11-13` reads — so AC2–AC7 can be tested without hosting. No new package: `Microsoft.Data.Sqlite` comes through the API project's `Microsoft.EntityFrameworkCore.Sqlite` reference (`HoaVoting.Api.csproj:20`), and the ASP.NET Core framework flows through the project reference (`HoaVoting.Tests.csproj:22`).

Step 1: write the failing test. It pins behaviour that exists today (tenancy, and `HydrateAsync` at `ProposalsController.cs:218-250` persisting Closed), so it only fails because the harness does not exist yet.

`tests/HoaVoting.Tests/ControllerHarnessTests.cs` (new file, full content):

````csharp
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
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: `error CS0246: The type or namespace name 'ControllerTestBase' could not be found`

Step 2: implement. The fake implements the interface as it is at the base (`IVocdoniClient.cs:3-36`, including `GetSubscriptionFeaturesAsync`; Tasks 2 and 3 edit it). Arrange/act use `Db`; assertions read through a fresh `NewDb()` so they see what was saved, not the change tracker.

`tests/HoaVoting.Tests/FakeVocdoniClient.cs` (new file, full content):

````csharp
using HoaVoting.Api.Services.Vocdoni;

namespace HoaVoting.Tests;

/// <summary>
/// Scripted <see cref="IVocdoniClient"/> for controller tests: every call is recorded in
/// <see cref="Calls"/> ("Method arg"), and each answer comes from a settable delegate, so a test
/// scripts a return value or throws from it.
/// </summary>
public sealed class FakeVocdoniClient : IVocdoniClient
{
    public List<string> Calls { get; } = [];

    public List<VocdoniOrgMember> Members { get; set; } = [];
    public CreateVotingProcessRequest? LastCreate { get; private set; }
    public List<VocdoniOrgMember>? LastAdded { get; private set; }

    public Func<CreateVotingProcessRequest, string> OnCreateProcess { get; set; } = _ => "proc1";
    public Action<string> OnPublish { get; set; } = _ => { };
    public Func<string, VotingProcessResponse> OnGetProcess { get; set; } = id => new() { Id = id };

    public Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"CreateOrganization {name}");
        return Task.FromResult("0xnew");
    }

    public Task DeleteOrganizationAsync(string orgAddress, CancellationToken ct = default)
    {
        Calls.Add($"DeleteOrganization {orgAddress}");
        return Task.CompletedTask;
    }

    public Task<AddMembersResponse> AddMembersAsync(string orgAddress, List<VocdoniOrgMember> members, CancellationToken ct = default)
    {
        Calls.Add($"AddMembers {orgAddress}");
        LastAdded = members;
        return Task.FromResult(new AddMembersResponse { Added = members.Count });
    }

    public Task<List<VocdoniOrgMember>> ListMembersAsync(string orgAddress, CancellationToken ct = default)
    {
        Calls.Add($"ListMembers {orgAddress}");
        return Task.FromResult(Members);
    }

    public Task DeleteMembersAsync(string orgAddress, List<string> memberIds, CancellationToken ct = default)
    {
        Calls.Add($"DeleteMembers {string.Join(",", memberIds)}");
        return Task.CompletedTask;
    }

    public Task<string> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
    {
        Calls.Add("CreateProcess");
        LastCreate = request;
        return Task.FromResult(OnCreateProcess(request));
    }

    public Task PublishVotingProcessAsync(string processId, CancellationToken ct = default)
    {
        Calls.Add($"Publish {processId}");
        OnPublish(processId);
        return Task.CompletedTask;
    }

    public Task<VotingProcessResponse> GetVotingProcessAsync(string processId, CancellationToken ct = default)
    {
        Calls.Add($"GetProcess {processId}");
        return Task.FromResult(OnGetProcess(processId));
    }

    public Task SetQuestionsStatusAsync(string processId, string status, List<string>? questionIds = null, CancellationToken ct = default)
    {
        Calls.Add($"SetQuestionsStatus {processId} {status}");
        return Task.CompletedTask;
    }

    public Task<UpdateQuestionCensusResponse> SetQuestionCensusAsync(string processId, string questionId, List<string> memberIds, CancellationToken ct = default)
    {
        Calls.Add($"SetQuestionCensus {processId} {questionId}");
        return Task.FromResult(new UpdateQuestionCensusResponse());
    }

    public Task<SubscriptionFeatures> GetSubscriptionFeaturesAsync(string orgAddress, CancellationToken ct = default)
    {
        Calls.Add($"GetSubscriptionFeatures {orgAddress}");
        return Task.FromResult(new SubscriptionFeatures());
    }
}
````

`tests/HoaVoting.Tests/ControllerTestBase.cs` (new file, full content):

````csharp
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
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: `Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31`

Step 3: commit
`git add tests/HoaVoting.Tests/FakeVocdoniClient.cs tests/HoaVoting.Tests/ControllerTestBase.cs tests/HoaVoting.Tests/ControllerHarnessTests.cs && git -c commit.gpgsign=false commit -m "test(api): add controller test harness" -- tests/HoaVoting.Tests/FakeVocdoniClient.cs tests/HoaVoting.Tests/ControllerTestBase.cs tests/HoaVoting.Tests/ControllerHarnessTests.cs`

Interfaces this task provides to later tasks: `ControllerTestBase` (`Db`, `NewDb()`, `Vocdoni`, `Assoc`, `Org`, `As/AsOwner/AsAdmin(controller)`, `SeedProposal(status, questions, published, endDate, processId)`, `PublishedProcess(id, params statuses)` with upstream ids `e0, e1, …`, `StatusOf(...)`, `ValueOf(...)`); `FakeVocdoniClient` (`Calls` as `"Method arg"`, `Members`, `LastCreate`, `LastAdded`, `On…` delegates).

### Task 2: Remove plan gating; readable upstream errors
Model: standard   Repo: same   Files: src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs, src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs, src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs, src/HoaVoting.Api/Services/Vocdoni/UpstreamErrors.cs, src/HoaVoting.Api/Program.cs, src/HoaVoting.Api/Controllers/AssociationsController.cs, src/HoaVoting.Api/Dtos/Dtos.cs, tests/HoaVoting.Tests/UpstreamErrorsTests.cs, tests/HoaVoting.Tests/VocdoniClientTests.cs, tests/HoaVoting.Tests/FakeVocdoniClient.cs, web/src/components/Proposals.jsx, docker-compose.yml
Goal: spec A + B (Goals 1 and 3, AC3, part of AC1). Deletes `GetSubscriptionFeaturesAsync`/`FindManagedOrgPlanIdAsync` (`VocdoniClient.cs:132-167`), the interface member (`IVocdoniClient.cs:34-35`), the subscription/plan wire models (`VocdoniModels.cs:265-307`; `ManagedOrganizationsResponse`/`ManagedOrganizationInfo` stay, trimmed to `Address`, for Task 3), `AssociationsController.Features` (`:103-121`), `AssociationFeaturesResponse` (`Dtos.cs:122-123`), its 4 tests (`VocdoniClientTests.cs:156-208`), the SPA fetch/disable (`Proposals.jsx:36-38, 54-56, 246-259`) and `Vocdoni__IntegratorAddress` (`docker-compose.yml:16`). `VocdoniApiException` (`VocdoniClient.cs:8-13`) parses `{error, code, data}`; the `Program.cs:61-74` lambda writes `{error, code, detail}` from `UpstreamErrors.Describe`.

Step 1: write the failing test

`tests/HoaVoting.Tests/UpstreamErrorsTests.cs` (new file, full content):

````csharp
using System.Net;
using HoaVoting.Api.Services.Vocdoni;

namespace HoaVoting.Tests;

// AC3: the 502 body's {code, detail} — readable sentences for the billing/limit codes, upstream's own
// text otherwise, the raw message when the body is not an errors.Error.
public class UpstreamErrorsTests
{
    private static VocdoniApiException Upstream(HttpStatusCode status, string body) => new(status, body);

    [Fact]
    public void Wallet_short_names_both_amounts_in_euros()
    {
        var (code, detail) = UpstreamErrors.Describe(Upstream(HttpStatusCode.PaymentRequired,
            """{"error":"insufficient integrator wallet balance","code":40175,"data":{"requiredCents":1980,"availableCents":500}}"""));

        Assert.Equal(40175, code);
        Assert.Equal("The integrator's wallet doesn't cover this (needs €19.80, available €5.00). Top it up in the Vocdoni portal.", detail);
    }

    [Fact]
    public void Wallet_short_without_amounts_drops_the_parenthesis()
    {
        var (code, detail) = UpstreamErrors.Describe(Upstream(HttpStatusCode.PaymentRequired,
            """{"error":"insufficient integrator wallet balance","code":40175,"data":{"requiredCents":1980}}"""));

        Assert.Equal(40175, code);
        Assert.Equal("The integrator's wallet doesn't cover this. Top it up in the Vocdoni portal.", detail);
    }

    [Fact]
    public void Managed_org_cap_asks_for_a_top_up()
    {
        var (code, detail) = UpstreamErrors.Describe(Upstream(HttpStatusCode.PaymentRequired,
            """{"error":"top up to create more managed organizations","code":40184}"""));

        Assert.Equal(40184, code);
        Assert.Equal("Top up the integrator wallet in the Vocdoni portal to create another association.", detail);
    }

    [Fact]
    public void Other_codes_pass_the_upstream_text_through()
    {
        var (code, detail) = UpstreamErrors.Describe(Upstream(HttpStatusCode.BadRequest,
            """{"error":"anonymous voting is temporarily unavailable","code":40037}"""));

        Assert.Equal(40037, code);
        Assert.Equal("anonymous voting is temporarily unavailable", detail);
    }

    [Fact]
    public void Non_json_body_keeps_the_raw_message()
    {
        var ex = Upstream(HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var (code, detail) = UpstreamErrors.Describe(ex);

        Assert.Null(code);
        Assert.Equal(ex.Message, detail);
        Assert.Null(ex.ErrorData);
    }

    [Fact]
    public void Transport_failure_keeps_its_message()
    {
        Assert.Equal((null, "connection refused"), UpstreamErrors.Describe(new HttpRequestException("connection refused")));
    }
}
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: `error CS0103: The name 'UpstreamErrors' does not exist in the current context`

Step 2: implement

`src/HoaVoting.Api/Services/Vocdoni/UpstreamErrors.cs` (new file, full content):

````csharp
using System.Globalization;
using System.Text.Json;

namespace HoaVoting.Api.Services.Vocdoni;

/// <summary>
/// Turns an upstream failure into the <c>{code, detail}</c> of the 502 body (Program.cs). The billing
/// and limit codes the SPA shows verbatim get a readable sentence; anything else passes upstream's own
/// <c>error</c> text through, or the raw message when the body was not an <c>errors.Error</c>.
/// </summary>
internal static class UpstreamErrors
{
    public static (int? Code, string Detail) Describe(Exception ex)
    {
        if (ex is not VocdoniApiException v) return (null, ex.Message);
        var detail = v.Code switch
        {
            40175 => $"The integrator's wallet doesn't cover this{Amounts(v.ErrorData)}. Top it up in the Vocdoni portal.",
            40183 => "This association reached the spending cap set by its integrator.",
            40184 => "Top up the integrator wallet in the Vocdoni portal to create another association.",
            40176 => "Over 50 000 voters: this proposal needs a custom quote from Vocdoni.",
            _ => string.IsNullOrEmpty(v.UpstreamError) ? v.Message : v.UpstreamError,
        };
        return (v.Code, detail);
    }

    // " (needs €19.80, available €5.00)", or "" unless data carries both amounts.
    private static string Amounts(JsonElement? data) =>
        Cents(data, "requiredCents") is { } need && Cents(data, "availableCents") is { } have
            ? $" (needs {Eur(need)}, available {Eur(have)})"
            : "";

    private static long? Cents(JsonElement? data, string name) =>
        data is { ValueKind: JsonValueKind.Object } d
        && d.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : null;

    private static string Eur(long cents) => (cents / 100m).ToString("€0.00", CultureInfo.InvariantCulture);
}
````

````diff
diff --git a/docker-compose.yml b/docker-compose.yml
index de04b78..ae0ba24 100644
--- a/docker-compose.yml
+++ b/docker-compose.yml
@@ -13,7 +13,6 @@ services:
       # browser can't resolve host.docker.internal, so point it at the host-published address.
       Vocdoni__BrowserUrl: ${VOCDONI_BROWSER_URL:-}
       Vocdoni__ApiToken: ${VOCDONI_API_TOKEN:-}
-      Vocdoni__IntegratorAddress: ${VOCDONI_INTEGRATOR_ADDRESS:-}
       # App auth + seeded admin.
       Jwt__SigningKey: ${JWT_SIGNING_KEY:-dev-only-insecure-signing-key-change-me-min-32-chars}
       Admin__Email: ${ADMIN_EMAIL:-admin@hoa.local}
diff --git a/src/HoaVoting.Api/Controllers/AssociationsController.cs b/src/HoaVoting.Api/Controllers/AssociationsController.cs
index 207c196..1793bb0 100644
--- a/src/HoaVoting.Api/Controllers/AssociationsController.cs
+++ b/src/HoaVoting.Api/Controllers/AssociationsController.cs
@@ -100,26 +100,6 @@ public class AssociationsController(AppDbContext db, IVocdoniClient vocdoni) : A
         return new AssociationResponse(assoc.Id, assoc.Name, assoc.Owner!.Email, assoc.VocdoniOrgAddress, assoc.CreatedAt);
     }
 
-    /// <summary>
-    /// Plan features relevant to the admin UI. Fetched lazily by the proposal form (not folded into
-    /// the association reads, which would put an upstream call on every page load): anonymous voting
-    /// is plan-gated upstream and its publish fails asynchronously + opaquely on unsupported plans,
-    /// so the UI disables the toggle instead.
-    /// </summary>
-    [Authorize]
-    [HttpGet("{id:int}/features")]
-    public async Task<ActionResult<AssociationFeaturesResponse>> Features(int id, CancellationToken ct)
-    {
-        var assoc = await db.Associations.FindAsync([id], ct);
-        if (assoc is null) return NotFound();
-        if (!Authorization.AssociationAccess.CanAccess(CurrentRole, CurrentUserId, assoc)) return Forbid();
-
-        // API-key integrators can't read the subscription directly; the client falls back to the
-        // managed-orgs planId + public /plans catalog, so this is a positive confirmation either way.
-        var features = await vocdoni.GetSubscriptionFeaturesAsync(assoc.VocdoniOrgAddress, ct);
-        return new AssociationFeaturesResponse(features.Anonymous);
-    }
-
     /// <summary>
     /// Removes an association: deletes the Vocdoni managed org (cascade — members, censuses,
     /// processes, bundles) and reclaims integrator quota, then drops its proposals and owner login
diff --git a/src/HoaVoting.Api/Dtos/Dtos.cs b/src/HoaVoting.Api/Dtos/Dtos.cs
index aec9474..c79f673 100644
--- a/src/HoaVoting.Api/Dtos/Dtos.cs
+++ b/src/HoaVoting.Api/Dtos/Dtos.cs
@@ -29,7 +29,7 @@ public record CreateProposalRequest(
     DateTimeOffset StartDate,
     DateTimeOffset EndDate,
     List<QuestionInput> Questions,
-    // Blind-CSP anonymous voting (saas-backend #641). Plan-gated: check /features first.
+    // Blind-CSP anonymous voting (saas-backend #641), on every plan (#718).
     bool Anonymous = false);
 
 public record QuestionInput(
@@ -119,9 +119,6 @@ public record PublicQuestion(
     int? Budget = null,
     int? CostExponent = null);
 
-/// <summary>Plan features relevant to the admin UI (from the org's Vocdoni subscription).</summary>
-public record AssociationFeaturesResponse(bool AnonymousVoting);
-
 // Live per-question eligibility (saas-backend #621). MemberIds is the COMPLETE desired list
 // (Vocdoni member ids); [] reopens the question to the whole census.
 public record SetEligibilityRequest(List<string> MemberIds);
diff --git a/src/HoaVoting.Api/Program.cs b/src/HoaVoting.Api/Program.cs
index 49cf232..fd74e81 100644
--- a/src/HoaVoting.Api/Program.cs
+++ b/src/HoaVoting.Api/Program.cs
@@ -63,8 +63,10 @@ app.UseExceptionHandler(eh => eh.Run(async ctx =>
     var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
     if (ex is VocdoniApiException or HttpRequestException)
     {
+        // Readable sentence for the billing/limit codes, else upstream's own error text (UpstreamErrors).
+        var (code, detail) = UpstreamErrors.Describe(ex);
         ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
-        await ctx.Response.WriteAsJsonAsync(new { error = "vocdoni_upstream", detail = ex.Message });
+        await ctx.Response.WriteAsJsonAsync(new { error = "vocdoni_upstream", code, detail });
     }
     else
     {
diff --git a/src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs b/src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs
index 8d255d1..2cf9d5f 100644
--- a/src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs
+++ b/src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs
@@ -30,7 +30,4 @@ public interface IVocdoniClient
     /// whole census; waits for the census-resize job when one is enqueued (202). Upstream 409 (code 40173,
     /// voter already signed) surfaces as a VocdoniApiException the caller can inspect.</summary>
     Task<UpdateQuestionCensusResponse> SetQuestionCensusAsync(string processId, string questionId, List<string> memberIds, CancellationToken ct = default);
-
-    /// <summary>Reads the org's subscription plan features (e.g. whether anonymous voting is allowed).</summary>
-    Task<SubscriptionFeatures> GetSubscriptionFeaturesAsync(string orgAddress, CancellationToken ct = default);
 }
diff --git a/src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs b/src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs
index 7a7b8a8..8f07161 100644
--- a/src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs
+++ b/src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs
@@ -8,8 +8,40 @@ namespace HoaVoting.Api.Services.Vocdoni;
 public sealed class VocdoniApiException(HttpStatusCode status, string body)
     : Exception($"Vocdoni API returned {(int)status} {status}: {body}")
 {
+    private readonly (int? Code, string? Error, JsonElement? Data) _parsed = Parse(body);
+
     public HttpStatusCode Status { get; } = status;
     public string Body { get; } = body;
+
+    /// <summary>Upstream <c>errors.Error.code</c> (e.g. 40175); null when the body is not that shape.</summary>
+    public int? Code => _parsed.Code;
+
+    /// <summary>Upstream <c>errors.Error.error</c> text; null when absent.</summary>
+    public string? UpstreamError => _parsed.Error;
+
+    /// <summary>Upstream <c>errors.Error.data</c> (e.g. requiredCents/availableCents); null when absent. Not
+    /// named Data: that would hide Exception.Data.</summary>
+    public JsonElement? ErrorData => _parsed.Data;
+
+    // Best-effort: the body is upstream input, so every read is guarded by ValueKind (JsonElement
+    // getters throw InvalidOperationException on a mismatched kind) and non-JSON yields all-null.
+    private static (int?, string?, JsonElement?) Parse(string body)
+    {
+        try
+        {
+            using var doc = JsonDocument.Parse(body);
+            var root = doc.RootElement;
+            if (root.ValueKind != JsonValueKind.Object) return default;
+            int? code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
+            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
+            JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
+            return (code, error, data);
+        }
+        catch (JsonException)
+        {
+            return default;
+        }
+    }
 }
 
 /// <summary>
@@ -129,43 +161,6 @@ public sealed class VocdoniClient(HttpClient http) : IVocdoniClient
         return resp;
     }
 
-    /// <summary>Reads the org's subscription plan features; a missing plan reads as all-off.
-    /// The subscription endpoint is JWT-only upstream (403 code 40157 for API keys), so on 403 the
-    /// features are positively confirmed the key-compatible way instead: the managed-orgs list
-    /// (managed:read) carries the org's planId, joined with the public /plans catalog.</summary>
-    public async Task<SubscriptionFeatures> GetSubscriptionFeaturesAsync(string orgAddress, CancellationToken ct = default)
-    {
-        try
-        {
-            var info = await SendAsync<OrganizationSubscriptionInfo>(
-                HttpMethod.Get, $"/organizations/{orgAddress}/subscription", null, ct);
-            return info.Plan?.Features ?? new SubscriptionFeatures();
-        }
-        catch (VocdoniApiException e) when (e.Status == HttpStatusCode.Forbidden)
-        {
-            var planId = await FindManagedOrgPlanIdAsync(orgAddress, ct);
-            if (string.IsNullOrEmpty(planId)) return new SubscriptionFeatures();
-            var plans = await SendAsync<List<VocdoniPlan>>(HttpMethod.Get, "/plans", null, ct);
-            return plans.FirstOrDefault(p => string.Equals(p.Id, planId, StringComparison.Ordinal))
-                ?.Features ?? new SubscriptionFeatures();
-        }
-    }
-
-    /// <summary>Walks the paginated managed-orgs list to find one org's subscription planId.</summary>
-    private async Task<string?> FindManagedOrgPlanIdAsync(string orgAddress, CancellationToken ct)
-    {
-        for (var page = 1; ; page++)
-        {
-            var resp = await SendAsync<ManagedOrganizationsResponse>(
-                HttpMethod.Get, $"/integrator/organizations?page={page}&limit=100", null, ct);
-            var org = resp.Organizations.FirstOrDefault(o =>
-                string.Equals(o.Address, orgAddress, StringComparison.OrdinalIgnoreCase));
-            if (org is not null) return org.Subscription?.PlanId;
-            if (resp.Organizations.Count == 0 || resp.Pagination is not { } p || p.CurrentPage >= p.LastPage)
-                return null;
-        }
-    }
-
     /// <summary>Polls GET /jobs/{id} until the async transaction completes; throws on failure or timeout.</summary>
     // ponytail: bounded poll (~40s), no webhook available. Move to a background worker for production.
     private async Task PollJobAsync(string jobId, CancellationToken ct)
diff --git a/src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs b/src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs
index c8ca8b2..a0d422d 100644
--- a/src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs
+++ b/src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs
@@ -262,26 +262,9 @@ public sealed class UpdateQuestionCensusResponse
     public int Removed { get; set; }
 }
 
-// --- organization subscription (plan feature gating) ------------------------
+// --- integrator ------------------------------------------------------------
 
-/// <summary>Subset of GET /organizations/{org}/subscription we read.</summary>
-public sealed class OrganizationSubscriptionInfo
-{
-    public SubscriptionPlan? Plan { get; set; }
-}
-
-public sealed class SubscriptionPlan
-{
-    public SubscriptionFeatures? Features { get; set; }
-}
-
-public sealed class SubscriptionFeatures
-{
-    /// <summary>Gates blind-CSP (and zk) anonymous elections. Publish fails asynchronously without it.</summary>
-    public bool Anonymous { get; set; }
-}
-
-/// <summary>Subset of GET /integrator/organizations (paginated) we read: each managed org's planId.</summary>
+/// <summary>Subset of GET /integrator/organizations (paginated) we read.</summary>
 public sealed class ManagedOrganizationsResponse
 {
     public List<ManagedOrganizationInfo> Organizations { get; set; } = new();
@@ -291,17 +274,4 @@ public sealed class ManagedOrganizationsResponse
 public sealed class ManagedOrganizationInfo
 {
     public string? Address { get; set; }
-    public ManagedOrgSubscription? Subscription { get; set; }
-}
-
-public sealed class ManagedOrgSubscription
-{
-    public string? PlanId { get; set; }
-}
-
-/// <summary>Subset of the public GET /plans catalog: plan id (matches subscription.planId) + features.</summary>
-public sealed class VocdoniPlan
-{
-    public string? Id { get; set; }
-    public SubscriptionFeatures? Features { get; set; }
 }
diff --git a/tests/HoaVoting.Tests/FakeVocdoniClient.cs b/tests/HoaVoting.Tests/FakeVocdoniClient.cs
index 6239704..bfe0a42 100644
--- a/tests/HoaVoting.Tests/FakeVocdoniClient.cs
+++ b/tests/HoaVoting.Tests/FakeVocdoniClient.cs
@@ -81,10 +81,4 @@ public sealed class FakeVocdoniClient : IVocdoniClient
         Calls.Add($"SetQuestionCensus {processId} {questionId}");
         return Task.FromResult(new UpdateQuestionCensusResponse());
     }
-
-    public Task<SubscriptionFeatures> GetSubscriptionFeaturesAsync(string orgAddress, CancellationToken ct = default)
-    {
-        Calls.Add($"GetSubscriptionFeatures {orgAddress}");
-        return Task.FromResult(new SubscriptionFeatures());
-    }
 }
diff --git a/tests/HoaVoting.Tests/VocdoniClientTests.cs b/tests/HoaVoting.Tests/VocdoniClientTests.cs
index aea15af..029e71a 100644
--- a/tests/HoaVoting.Tests/VocdoniClientTests.cs
+++ b/tests/HoaVoting.Tests/VocdoniClientTests.cs
@@ -153,60 +153,6 @@ public class VocdoniClientTests
         Assert.Equal("/processes/proc1/questions/status", Assert.Single(handler.Paths));
     }
 
-    [Fact]
-    public async Task GetSubscriptionFeatures_reads_plan_features()
-    {
-        var handler = new CapturingHandler(HttpStatusCode.OK,
-            """{"plan":{"features":{"anonymous":true,"overwrite":false}}}""");
-        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
-
-        var features = await client.GetSubscriptionFeaturesAsync("0xabc");
-
-        Assert.True(features.Anonymous);
-        Assert.Equal("/organizations/0xabc/subscription", handler.LastPath);
-    }
-
-    [Fact]
-    public async Task GetSubscriptionFeatures_reads_missing_plan_as_all_off()
-    {
-        var handler = new CapturingHandler(HttpStatusCode.OK, """{"plan":null}""");
-        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
-
-        Assert.False((await client.GetSubscriptionFeaturesAsync("0xabc")).Anonymous);
-    }
-
-    [Fact]
-    public async Task GetSubscriptionFeatures_resolves_via_managed_orgs_and_plans_on_403()
-    {
-        // The subscription read is JWT-only upstream (403/40157 for API keys). Positive confirmation
-        // instead: managed-orgs list carries the org's planId; the public /plans catalog carries the
-        // plan's features. Address matching is case-insensitive (hex).
-        var handler = new SequenceHandler(
-            (HttpStatusCode.Forbidden, """{"error":"API keys are not permitted for this endpoint","code":40157}"""),
-            (HttpStatusCode.OK, """{"organizations":[{"address":"0xABC","subscription":{"planId":"prod_free"}}],"pagination":{"currentPage":1,"lastPage":1}}"""),
-            (HttpStatusCode.OK, """[{"id":"prod_pro","features":{"anonymous":false}},{"id":"prod_free","features":{"anonymous":true}}]"""));
-        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
-
-        var features = await client.GetSubscriptionFeaturesAsync("0xabc");
-
-        Assert.True(features.Anonymous);
-        Assert.Equal(3, handler.Calls);
-        Assert.Equal("/plans", handler.Paths[^1]);
-    }
-
-    [Fact]
-    public async Task GetSubscriptionFeatures_reads_unknown_org_or_plan_as_all_off_on_403()
-    {
-        // Org missing from the managed list ⇒ no planId ⇒ all-off (never optimistic).
-        var handler = new SequenceHandler(
-            (HttpStatusCode.Forbidden, """{"code":40157}"""),
-            (HttpStatusCode.OK, """{"organizations":[{"address":"0xother","subscription":{"planId":"prod_free"}}],"pagination":{"currentPage":1,"lastPage":1}}"""));
-        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
-
-        Assert.False((await client.GetSubscriptionFeaturesAsync("0xabc")).Anonymous);
-        Assert.Equal(2, handler.Calls); // /plans never fetched without a planId
-    }
-
     [Fact]
     public async Task CreateVotingProcess_sends_census_anonymous_only_when_set()
     {
diff --git a/web/src/components/Proposals.jsx b/web/src/components/Proposals.jsx
index 1d84a8e..bf3a931 100644
--- a/web/src/components/Proposals.jsx
+++ b/web/src/components/Proposals.jsx
@@ -33,9 +33,6 @@ export default function Proposals({ assoc }) {
   const [error, setError] = useState('')
   const [busy, setBusy] = useState(false)
   const [closing, setClosing] = useState(() => new Set()) // proposal ids with a pending close
-  // Anonymous voting is plan-gated upstream and publish fails opaquely without it — fetch the flag
-  // (best-effort: an unreachable read just leaves the toggle disabled).
-  const [anonymousAllowed, setAnonymousAllowed] = useState(false)
 
   const base = `/associations/${assoc.id}/proposals`
 
@@ -51,9 +48,6 @@ export default function Proposals({ assoc }) {
   }
   useEffect(() => {
     load()
-    api(`/associations/${assoc.id}/features`)
-      .then((f) => setAnonymousAllowed(!!f?.anonymousVoting))
-      .catch(() => setAnonymousAllowed(false))
   }, [assoc.id])
 
   // Question + choice editing (functional updates — no stale closures).
@@ -243,19 +237,9 @@ export default function Proposals({ assoc }) {
             <button type="button" className="link" onClick={addQuestion}>+ Add question</button>
           </div>
 
-          <label
-            className="check"
-            title={anonymousAllowed
-              ? 'Blind-signature voting: the census authority cannot link voters to ballots.'
-              : 'Anonymous voting is not included in this organization’s plan.'}
-          >
-            <input
-              type="checkbox"
-              checked={form.anonymous}
-              disabled={!anonymousAllowed}
-              onChange={(e) => setForm({ ...form, anonymous: e.target.checked })}
-            />
-            <span>Anonymous voting{anonymousAllowed ? '' : ' (not in your plan)'}</span>
+          <label className="check" title="Blind-signature voting: the census authority cannot link voters to ballots.">
+            <input type="checkbox" checked={form.anonymous} onChange={(e) => setForm({ ...form, anonymous: e.target.checked })} />
+            <span>Anonymous voting</span>
           </label>
 
           {error && <div className="error">{error}</div>}
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test` then `cd web && npm ci && npm run build` (skip `npm ci` if `node_modules` is current)
Expected: `Passed!  - Failed:     0, Passed:    33, Skipped:     0, Total:    33` with no CS0114 warning; vite `✓ built`

Step 3: commit
`git add src/HoaVoting.Api/Services/Vocdoni/UpstreamErrors.cs tests/HoaVoting.Tests/UpstreamErrorsTests.cs && git -c commit.gpgsign=false commit -m "fix(api): drop removed plan gating and describe upstream errors" -- src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs src/HoaVoting.Api/Services/Vocdoni/UpstreamErrors.cs src/HoaVoting.Api/Program.cs src/HoaVoting.Api/Controllers/AssociationsController.cs src/HoaVoting.Api/Dtos/Dtos.cs tests/HoaVoting.Tests/UpstreamErrorsTests.cs tests/HoaVoting.Tests/VocdoniClientTests.cs tests/HoaVoting.Tests/FakeVocdoniClient.cs web/src/components/Proposals.jsx docker-compose.yml`

Interfaces this task provides to later tasks: `VocdoniApiException.Code` (`int?`), `.UpstreamError` (`string?`), `.ErrorData` (`JsonElement?`); `internal static (int? Code, string Detail) UpstreamErrors.Describe(Exception)`.

### Task 3: VocdoniClient job, delete, end-date, pricing, integrator, wallet and member-upsert calls
Model: standard   Repo: same   Files: src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs, src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs, src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs, src/HoaVoting.Api/Controllers/ProposalsController.cs, tests/HoaVoting.Tests/VocdoniClientTests.cs, tests/HoaVoting.Tests/FakeVocdoniClient.cs
Goal: the client half of spec C/D/E/F. `PublishVotingProcessAsync` (`VocdoniClient.cs:88-97`) returns the job id (null on 200) without polling; `CreateVotingProcessAsync` (`:76-82`) returns the whole `CreateVotingProcessResponse` (+ `missingData`); `PollJobAsync` (`:171-183`) becomes a private wrapper over the new `WaitForJobAsync` that still throws on Failed/Pending, so `SetQuestionCensusAsync` (`:122-130`) is unchanged. New: `DeleteVotingProcessAsync`, `GetJobAsync`, `WaitForJobAsync`, `SetProcessEndDateAsync`, `GetPriceAsync`, `GetIntegratorInfoAsync`, `GetIntegratorAddressAsync`, `GetWalletAsync`, `UpsertMemberAsync`; `VocdoniOrgMember` (`VocdoniModels.cs:24-37`) gains write-only `Password` and read-only `HasPassword`. Wire shapes per the swagger: `/pricing` → `apicommon.ProcessPriceResponse`, `/integrator` → `IntegratorInfoResponse`, wallet → `WalletResponse` (`balanceCents`, `currency`), managed org `managedBy` (hex string), `PUT /processes/{id}/endDate {endDate}` → `EnqueuedResponse`, `PUT /organizations/{org}/members` → `UpsertOrgMemberRequest`. The `ProposalsController.cs:59-60` call site gets three interim lines that keep today's wait-then-read behaviour; Task 5 replaces them.

Step 1: write the failing test (replaces the three publish-poll tests at `VocdoniClientTests.cs:103-139`, adds 12)

````diff
diff --git a/tests/HoaVoting.Tests/VocdoniClientTests.cs b/tests/HoaVoting.Tests/VocdoniClientTests.cs
index 029e71a..a7920ca 100644
--- a/tests/HoaVoting.Tests/VocdoniClientTests.cs
+++ b/tests/HoaVoting.Tests/VocdoniClientTests.cs
@@ -48,9 +48,9 @@ public class VocdoniClientTests
         var handler = new CapturingHandler(HttpStatusCode.OK, """{"processId":"6a42"}""");
         var client = new VocdoniClient(ClientWithToken(handler, "tok"));
 
-        var id = await client.CreateVotingProcessAsync(new CreateVotingProcessRequest { OrgAddress = "0xabc" });
+        var created = await client.CreateVotingProcessAsync(new CreateVotingProcessRequest { OrgAddress = "0xabc" });
 
-        Assert.Equal("6a42", id);
+        Assert.Equal("6a42", created.ProcessId);
         Assert.Equal(HttpMethod.Post, handler.LastMethod);
         Assert.Equal("/processes", handler.LastPath);
     }
@@ -101,41 +101,155 @@ public class VocdoniClientTests
     }
 
     [Fact]
-    public async Task PublishVotingProcess_polls_job_until_complete()
+    public async Task CreateVotingProcess_returns_missing_data()
     {
-        // 202 enqueue at /processes/{id}/publish → poll the batch-publish job until it completes.
-        var handler = new SequenceHandler(
-            (HttpStatusCode.Accepted, """{"jobId":"job1"}"""),
-            (HttpStatusCode.OK, """{"status":"completed"}"""));
+        var handler = new CapturingHandler(HttpStatusCode.OK, """{"processId":"6a42","missingData":["m3"]}""");
         var client = new VocdoniClient(ClientWithToken(handler, "tok"));
 
-        await client.PublishVotingProcessAsync("proc1");
+        var created = await client.CreateVotingProcessAsync(new CreateVotingProcessRequest { OrgAddress = "0xabc" });
+
+        Assert.Equal(["m3"], created.MissingData!);
+    }
+
+    [Fact]
+    public async Task PublishVotingProcess_returns_the_job_without_polling()
+    {
+        // 202 enqueue → hand the job id back; the caller decides how long to wait (pay-per-process
+        // publish can stay pending for hours).
+        var handler = new SequenceHandler((HttpStatusCode.Accepted, """{"jobId":"job1"}"""));
+        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
 
-        Assert.Equal("/processes/proc1/publish", handler.Paths[0]);
-        Assert.Equal("/jobs/job1", handler.Paths[^1]);
+        Assert.Equal("job1", await client.PublishVotingProcessAsync("proc1"));
+        Assert.Equal(["/processes/proc1/publish"], handler.Paths);
     }
 
     [Fact]
-    public async Task PublishVotingProcess_returns_immediately_when_already_published()
+    public async Task PublishVotingProcess_returns_null_when_already_published()
     {
-        // 200 idempotent path: no job poll needed.
         var handler = new SequenceHandler((HttpStatusCode.OK, """{"processId":"proc1"}"""));
         var client = new VocdoniClient(ClientWithToken(handler, "tok"));
 
-        await client.PublishVotingProcessAsync("proc1");
+        Assert.Null(await client.PublishVotingProcessAsync("proc1"));
         Assert.Equal(1, handler.Calls);
     }
 
     [Fact]
-    public async Task PublishVotingProcess_throws_when_job_fails()
+    public async Task PublishVotingProcess_surfaces_a_refusal_with_its_code()
     {
-        var handler = new SequenceHandler(
-            (HttpStatusCode.Accepted, """{"jobId":"job1"}"""),
-            (HttpStatusCode.OK, """{"status":"failed","errors":["out of quota"]}"""));
+        var handler = new SequenceHandler((HttpStatusCode.PaymentRequired,
+            """{"error":"insufficient balance","code":40175,"data":{"requiredCents":1980,"availableCents":0}}"""));
         var client = new VocdoniClient(ClientWithToken(handler, "tok"));
 
         var ex = await Assert.ThrowsAsync<VocdoniApiException>(() => client.PublishVotingProcessAsync("proc1"));
-        Assert.Contains("out of quota", ex.Message);
+        Assert.Equal(40175, ex.Code);
+        Assert.Equal(1980, ex.ErrorData!.Value.GetProperty("requiredCents").GetInt32());
+    }
+
+    [Fact]
+    public async Task DeleteVotingProcess_deletes_the_draft()
+    {
+        var handler = new CapturingHandler(HttpStatusCode.OK, "\"ok\"");
+        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
+
+        await client.DeleteVotingProcessAsync("proc1");
+
+        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
+        Assert.Equal("/processes/proc1", handler.LastPath);
+    }
+
+    [Fact]
+    public async Task WaitForJob_reports_completed_failed_and_pending()
+    {
+        var done = new VocdoniClient(ClientWithToken(new SequenceHandler(
+            (HttpStatusCode.OK, """{"status":"pending"}"""),
+            (HttpStatusCode.OK, """{"status":"completed"}""")), "tok")) { PollDelay = TimeSpan.Zero };
+        Assert.Equal((JobState.Completed, null), await done.WaitForJobAsync("j1"));
+
+        var failed = new VocdoniClient(ClientWithToken(new SequenceHandler(
+            (HttpStatusCode.OK, """{"status":"failed","errors":["out of funds","chain down"]}""")), "tok"));
+        Assert.Equal((JobState.Failed, "out of funds; chain down"), await failed.WaitForJobAsync("j1"));
+
+        var stuck = new SequenceHandler((HttpStatusCode.OK, """{"status":"pending"}"""));
+        var pending = new VocdoniClient(ClientWithToken(stuck, "tok")) { PollDelay = TimeSpan.Zero };
+        Assert.Equal((JobState.Pending, null), await pending.WaitForJobAsync("j1"));
+        Assert.Equal(20, stuck.Calls); // bounded poll, then give up without throwing
+    }
+
+    [Fact]
+    public async Task SetProcessEndDate_puts_rfc3339_utc_and_returns_the_job()
+    {
+        var handler = new CapturingHandler(HttpStatusCode.Accepted, """{"jobId":"j9"}""");
+        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
+
+        var job = await client.SetProcessEndDateAsync("p1", new DateTimeOffset(2026, 12, 31, 23, 59, 59, 500, TimeSpan.FromHours(2)));
+
+        Assert.Equal("j9", job);
+        Assert.Equal(HttpMethod.Put, handler.LastMethod);
+        Assert.Equal("/processes/p1/endDate", handler.LastPath);
+        Assert.Equal("""{"endDate":"2026-12-31T21:59:59Z"}""", handler.LastBody);
+    }
+
+    [Fact]
+    public async Task GetPrice_forwards_the_voter_count()
+    {
+        var handler = new CapturingHandler(HttpStatusCode.OK,
+            """{"lines":[{"kind":"base","description":"30 voters","amountCents":1980}],"totalCents":1980,"currency":"eur","quoteRecommended":false,"quoteRequired":false}""");
+        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
+
+        var price = await client.GetPriceAsync(30);
+
+        Assert.Equal((1980, "eur"), (price.TotalCents, price.Currency));
+        Assert.Equal("/pricing?voters=30", handler.LastPathAndQuery);
+    }
+
+    [Fact]
+    public async Task Integrator_address_is_managedBy_of_the_first_managed_org()
+    {
+        var handler = new CapturingHandler(HttpStatusCode.OK,
+            """{"organizations":[{"address":"0xorg","managedBy":"0xintegrator"}],"pagination":{"currentPage":1,"lastPage":3}}""");
+        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
+
+        Assert.Equal("0xintegrator", await client.GetIntegratorAddressAsync());
+        Assert.Equal("/integrator/organizations?page=1&limit=1", handler.LastPathAndQuery);
+
+        var none = new VocdoniClient(ClientWithToken(new CapturingHandler(HttpStatusCode.OK, """{"organizations":[]}"""), "tok"));
+        Assert.Null(await none.GetIntegratorAddressAsync());
+    }
+
+    [Fact]
+    public async Task Integrator_info_and_wallet_read_their_fields()
+    {
+        var info = await new VocdoniClient(ClientWithToken(new CapturingHandler(HttpStatusCode.OK,
+            """{"enabled":true,"canCreateManagedOrg":false,"managedOrgsCreated":1,"netTopUpCents":0}"""), "tok")).GetIntegratorInfoAsync();
+        Assert.False(info.CanCreateManagedOrg);
+        Assert.Equal(1, info.ManagedOrgsCreated);
+
+        var handler = new CapturingHandler(HttpStatusCode.OK, """{"balanceCents":12345,"currency":"eur","history":[]}""");
+        var wallet = await new VocdoniClient(ClientWithToken(handler, "tok")).GetWalletAsync("0xintegrator");
+        Assert.Equal((12345L, "eur"), (wallet.BalanceCents, wallet.Currency));
+        Assert.Equal("/organizations/0xintegrator/wallet?page=1&limit=1", handler.LastPathAndQuery);
+    }
+
+    [Fact]
+    public async Task UpsertMember_sends_only_id_and_password()
+    {
+        var handler = new CapturingHandler(HttpStatusCode.OK, """{"id":"m1"}""");
+        var client = new VocdoniClient(ClientWithToken(handler, "tok"));
+
+        await client.UpsertMemberAsync("0xabc", new VocdoniOrgMember { Id = "m1", Password = new string('a', 64) });
+
+        Assert.Equal(HttpMethod.Put, handler.LastMethod);
+        Assert.Equal("/organizations/0xabc/members", handler.LastPath);
+        Assert.Equal($$"""{"id":"m1","password":"{{new string('a', 64)}}"}""", handler.LastBody);
+    }
+
+    [Fact]
+    public async Task ListMembers_reads_hasPassword()
+    {
+        var handler = new CapturingHandler(HttpStatusCode.OK, """{"members":[{"id":"1","hasPassword":true},{"id":"2"}]}""");
+        var members = await new VocdoniClient(ClientWithToken(handler, "tok")).ListMembersAsync("0xabc");
+
+        Assert.Equal([true, false], members.Select(m => m.HasPassword));
     }
 
     [Fact]
@@ -243,6 +357,7 @@ public class VocdoniClientTests
         public string? LastAuthorization { get; private set; }
         public HttpMethod? LastMethod { get; private set; }
         public string? LastPath { get; private set; }
+        public string? LastPathAndQuery { get; private set; }
         public string? LastBody { get; private set; }
 
         protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
@@ -251,6 +366,7 @@ public class VocdoniClientTests
             LastAuthorization = request.Headers.Authorization?.ToString();
             LastMethod = request.Method;
             LastPath = request.RequestUri?.AbsolutePath;
+            LastPathAndQuery = request.RequestUri?.PathAndQuery;
             LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
             return new HttpResponseMessage(code)
             {
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: build errors, e.g. `error CS1061: 'string' does not contain a definition for 'MissingData'` and `error CS1061: 'VocdoniClient' does not contain a definition for 'DeleteVotingProcessAsync'`

Step 2: implement

````diff
diff --git a/src/HoaVoting.Api/Controllers/ProposalsController.cs b/src/HoaVoting.Api/Controllers/ProposalsController.cs
index 30ac483..49f66f5 100644
--- a/src/HoaVoting.Api/Controllers/ProposalsController.cs
+++ b/src/HoaVoting.Api/Controllers/ProposalsController.cs
@@ -56,8 +56,11 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
             Questions = req.Questions.Select(ToQuestionRequest).ToList(),
         };
 
-        var processId = await vocdoni.CreateVotingProcessAsync(request, ct);
-        await vocdoni.PublishVotingProcessAsync(processId, ct);
+        var processId = (await vocdoni.CreateVotingProcessAsync(request, ct)).ProcessId!;
+        // Interim (Task 5 replaces this with the publish routine): today's wait-then-read behaviour.
+        if (await vocdoni.PublishVotingProcessAsync(processId, ct) is { } jobId
+            && await vocdoni.WaitForJobAsync(jobId, ct) is var (state, jobError) && state != JobState.Completed)
+            throw new VocdoniApiException(System.Net.HttpStatusCode.BadGateway, $"publish job {state}: {jobError}");
         // Read back to capture each question's on-chain id + status (assigned during the batch publish).
         var published = await vocdoni.GetVotingProcessAsync(processId, ct);
 
diff --git a/src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs b/src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs
index 2cf9d5f..8738b92 100644
--- a/src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs
+++ b/src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs
@@ -11,13 +11,19 @@ public interface IVocdoniClient
     Task<List<VocdoniOrgMember>> ListMembersAsync(string orgAddress, CancellationToken ct = default);
     Task DeleteMembersAsync(string orgAddress, List<string> memberIds, CancellationToken ct = default);
 
+    /// <summary>Upserts one member; null fields keep their stored values. An unused id creates a member.</summary>
+    Task UpsertMemberAsync(string orgAddress, VocdoniOrgMember member, CancellationToken ct = default);
+
     // --- multi-question /processes API (saas-backend #571) -----------------
 
-    /// <summary>Creates a draft voting process (container with N questions); returns its 24-hex ProcessID.</summary>
-    Task<string> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default);
+    /// <summary>Creates a draft voting process (container with N questions): processId + missingData.</summary>
+    Task<CreateVotingProcessResponse> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default);
+
+    /// <summary>Starts the on-chain batch publish; returns its job id, or null when already published (200). Does not wait.</summary>
+    Task<string?> PublishVotingProcessAsync(string processId, CancellationToken ct = default);
 
-    /// <summary>Publishes every question of a process on-chain (async batch) and waits for the job.</summary>
-    Task PublishVotingProcessAsync(string processId, CancellationToken ct = default);
+    /// <summary>Deletes an unpublished draft; 409 once a publish submitted elections, 404 when gone.</summary>
+    Task DeleteVotingProcessAsync(string processId, CancellationToken ct = default);
 
     /// <summary>Reads a process fully hydrated — each question carries its on-chain upstreamId + status, and
     /// (saas-backend #596) an inline tally once it reaches "results" status.</summary>
@@ -30,4 +36,22 @@ public interface IVocdoniClient
     /// whole census; waits for the census-resize job when one is enqueued (202). Upstream 409 (code 40173,
     /// voter already signed) surfaces as a VocdoniApiException the caller can inspect.</summary>
     Task<UpdateQuestionCensusResponse> SetQuestionCensusAsync(string processId, string questionId, List<string> memberIds, CancellationToken ct = default);
+
+    /// <summary>Extends the process end date (saas-backend #844); returns the job id.</summary>
+    Task<string> SetProcessEndDateAsync(string processId, DateTimeOffset endDate, CancellationToken ct = default);
+
+    /// <summary>One read of GET /jobs/{id}, no polling.</summary>
+    Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken ct = default);
+
+    /// <summary>Bounded poll of a job: Completed, Failed (with the joined errors), or Pending when it outlived the poll.</summary>
+    Task<(JobState State, string? Error)> WaitForJobAsync(string jobId, CancellationToken ct = default);
+
+    // --- pricing + integrator wallet (saas-backend #819) -----------------------
+
+    Task<ProcessPrice> GetPriceAsync(int voters, CancellationToken ct = default);
+    Task<IntegratorInfo> GetIntegratorInfoAsync(CancellationToken ct = default);
+
+    /// <summary>The integrator org address (managedBy of its first managed org); null when it manages none.</summary>
+    Task<string?> GetIntegratorAddressAsync(CancellationToken ct = default);
+    Task<WalletInfo> GetWalletAsync(string orgAddress, CancellationToken ct = default);
 }
diff --git a/src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs b/src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs
index 8f07161..dafe02e 100644
--- a/src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs
+++ b/src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs
@@ -1,3 +1,4 @@
+using System.Globalization;
 using System.Net;
 using System.Net.Http.Json;
 using System.Text.Json;
@@ -102,32 +103,44 @@ public sealed class VocdoniClient(HttpClient http) : IVocdoniClient
         SendAsync(HttpMethod.Delete, $"/organizations/{orgAddress}/members",
             new DeleteMembersRequest { Ids = memberIds }, ct);
 
+    /// <summary>PUT /organizations/{org}/members: upsert one member. Fields left null are omitted on the wire
+    /// and keep their stored values. NB: an unused id CREATES a member — callers check it exists first.</summary>
+    public Task UpsertMemberAsync(string orgAddress, VocdoniOrgMember member, CancellationToken ct = default) =>
+        SendAsync(HttpMethod.Put, $"/organizations/{orgAddress}/members", member, ct);
+
     // --- multi-question /processes API (saas-backend #571) -----------------
 
-    /// <summary>Creates a draft voting process (container with N questions); returns its 24-hex ProcessID.</summary>
-    public async Task<string> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
+    /// <summary>Creates a draft voting process (container with N questions): its 24-hex ProcessID plus
+    /// <c>missingData</c>, the members left out of the census for lacking a required auth field.</summary>
+    public async Task<CreateVotingProcessResponse> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
     {
         var resp = await SendAsync<CreateVotingProcessResponse>(HttpMethod.Post, "/processes", request, ct);
         if (string.IsNullOrEmpty(resp.ProcessId))
             throw new VocdoniApiException(HttpStatusCode.BadGateway, "POST /processes returned no processId");
-        return resp.ProcessId!;
+        return resp;
     }
 
     /// <summary>
-    /// Publishes every question of a process on-chain as one atomic batch and waits for the job. Each
-    /// question becomes its own election; read the process back to get the per-question upstream ids.
+    /// Starts publishing every question of a process on-chain as one batch (pay-per-process: this is
+    /// the call that debits the wallet). Returns the job id to follow, or null when upstream answered
+    /// 200 (already published). Does not wait: the job can stay pending for minutes to hours.
     /// </summary>
-    public async Task PublishVotingProcessAsync(string processId, CancellationToken ct = default)
+    public async Task<string?> PublishVotingProcessAsync(string processId, CancellationToken ct = default)
     {
         using var resp = await SendCoreAsync(HttpMethod.Post, $"/processes/{processId}/publish", null, ct);
-        if (resp.StatusCode == HttpStatusCode.OK) return; // 200 = already published (idempotent)
+        if (resp.StatusCode == HttpStatusCode.OK) return null; // 200 = already published (idempotent)
 
         var enqueued = await ReadJsonAsync<EnqueuedResponse>(resp, ct);
         if (string.IsNullOrEmpty(enqueued.JobId))
             throw new VocdoniApiException(resp.StatusCode, "publish returned neither a result nor a jobId");
-        await PollJobAsync(enqueued.JobId!, ct);
+        return enqueued.JobId;
     }
 
+    /// <summary>DELETE /processes/{id}: deletes an unpublished draft (credits back a paid one). 409 once
+    /// a publish submitted elections (40903/40904), 404 when already gone.</summary>
+    public Task DeleteVotingProcessAsync(string processId, CancellationToken ct = default) =>
+        SendAsync(HttpMethod.Delete, $"/processes/{processId}", null, ct);
+
     /// <summary>Reads a process fully hydrated — each question carries its on-chain upstreamId + status, and
     /// (saas-backend #596) an inline tally once it reaches "results" status.</summary>
     public Task<VotingProcessResponse> GetVotingProcessAsync(string processId, CancellationToken ct = default) =>
@@ -161,22 +174,76 @@ public sealed class VocdoniClient(HttpClient http) : IVocdoniClient
         return resp;
     }
 
-    /// <summary>Polls GET /jobs/{id} until the async transaction completes; throws on failure or timeout.</summary>
-    // ponytail: bounded poll (~40s), no webhook available. Move to a background worker for production.
-    private async Task PollJobAsync(string jobId, CancellationToken ct)
+    /// <summary>PUT /processes/{id}/endDate (saas-backend #844): extend every open question's end; returns the job id.</summary>
+    public async Task<string> SetProcessEndDateAsync(string processId, DateTimeOffset endDate, CancellationToken ct = default)
+    {
+        var body = new SetProcessEndDateRequest
+        {
+            // RFC 3339 in UTC; upstream truncates to whole seconds anyway.
+            EndDate = endDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
+        };
+        var enqueued = await SendAsync<EnqueuedResponse>(HttpMethod.Put, $"/processes/{processId}/endDate", body, ct);
+        return string.IsNullOrEmpty(enqueued.JobId)
+            ? throw new VocdoniApiException(HttpStatusCode.BadGateway, "endDate change returned no jobId")
+            : enqueued.JobId!;
+    }
+
+    // --- jobs ----------------------------------------------------------------
+
+    /// <summary>One non-polling read of GET /jobs/{id} (the reconcile uses it).</summary>
+    public Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken ct = default) =>
+        SendAsync<JobStatusResponse>(HttpMethod.Get, $"/jobs/{jobId}", null, ct);
+
+    /// <summary>Delay between job polls. Tests set it to zero.</summary>
+    internal TimeSpan PollDelay { get; init; } = TimeSpan.FromSeconds(2);
+
+    /// <summary>Polls the job up to 20 times; Pending when it is still running after that. Error = the job's errors joined.</summary>
+    // ponytail: bounded poll (~40 s, under nginx's 60 s proxy timeout), no webhook available. Move to a
+    // background worker for production; until then the read-side reconcile settles what outlives it.
+    public async Task<(JobState State, string? Error)> WaitForJobAsync(string jobId, CancellationToken ct = default)
     {
         for (var attempt = 0; attempt < 20; attempt++)
         {
-            var job = await SendAsync<JobStatusResponse>(HttpMethod.Get, $"/jobs/{jobId}", null, ct);
-            if (job.Status == "completed")
-                return;
-            if (job.Status == "failed")
-                throw new VocdoniApiException(HttpStatusCode.BadGateway, $"job {jobId} failed: {string.Join("; ", job.Errors ?? [])}");
-            await Task.Delay(TimeSpan.FromSeconds(2), ct);
+            if (attempt > 0) await Task.Delay(PollDelay, ct);
+            var job = await GetJobAsync(jobId, ct);
+            if (job.Status == "completed") return (JobState.Completed, null);
+            if (job.Status == "failed") return (JobState.Failed, string.Join("; ", job.Errors ?? []));
         }
-        throw new VocdoniApiException(HttpStatusCode.GatewayTimeout, $"job {jobId} did not complete within the timeout");
+        return (JobState.Pending, null);
     }
 
+    /// <summary>Waits for the job and throws unless it completed (callers that need the outcome inline).</summary>
+    private async Task PollJobAsync(string jobId, CancellationToken ct)
+    {
+        var (state, error) = await WaitForJobAsync(jobId, ct);
+        if (state == JobState.Failed)
+            throw new VocdoniApiException(HttpStatusCode.BadGateway, $"job {jobId} failed: {error}");
+        if (state == JobState.Pending)
+            throw new VocdoniApiException(HttpStatusCode.GatewayTimeout, $"job {jobId} did not complete within the timeout");
+    }
+
+    // --- pricing + integrator wallet (saas-backend #819) -----------------------
+
+    /// <summary>GET /pricing?voters=N — the public pay-per-process calculator (voters ≥ 1).</summary>
+    public Task<ProcessPrice> GetPriceAsync(int voters, CancellationToken ct = default) =>
+        SendAsync<ProcessPrice>(HttpMethod.Get, $"/pricing?voters={voters}", null, ct);
+
+    /// <summary>GET /integrator — whether another managed org may be created now (quota:read).</summary>
+    public Task<IntegratorInfo> GetIntegratorInfoAsync(CancellationToken ct = default) =>
+        SendAsync<IntegratorInfo>(HttpMethod.Get, "/integrator", null, ct);
+
+    /// <summary>The integrator org's address: the <c>managedBy</c> of its first managed org (managed:read);
+    /// null when it manages none yet.</summary>
+    public async Task<string?> GetIntegratorAddressAsync(CancellationToken ct = default)
+    {
+        var resp = await SendAsync<ManagedOrganizationsResponse>(HttpMethod.Get, "/integrator/organizations?page=1&limit=1", null, ct);
+        return resp.Organizations.FirstOrDefault()?.ManagedBy is { Length: > 0 } addr ? addr : null;
+    }
+
+    /// <summary>GET /organizations/{org}/wallet — balance only (one history row requested). quota:read of that org.</summary>
+    public Task<WalletInfo> GetWalletAsync(string orgAddress, CancellationToken ct = default) =>
+        SendAsync<WalletInfo>(HttpMethod.Get, $"/organizations/{orgAddress}/wallet?page=1&limit=1", null, ct);
+
     // --- transport ---------------------------------------------------------
 
     private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
diff --git a/src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs b/src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs
index a0d422d..0c613d5 100644
--- a/src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs
+++ b/src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs
@@ -34,6 +34,14 @@ public sealed class VocdoniOrgMember
 
     /// <summary>Census weight (string per the API).</summary>
     public string? Weight { get; set; }
+
+    /// <summary>Write-only (saas-backend #760): the 64-lowercase-hex client hash of the member's password,
+    /// computed in the browser (hashMemberPassword). Never returned; null ⇒ omitted ⇒ kept on upsert.</summary>
+    public string? Password { get; set; }
+
+    /// <summary>Read-only: whether the member has a password. Not sent on writes.</summary>
+    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
+    public bool HasPassword { get; set; }
 }
 
 public sealed class AddMembersRequest
@@ -158,6 +166,8 @@ public sealed class EligibilitySpec
 public sealed class CreateVotingProcessResponse
 {
     public string? ProcessId { get; set; }
+    /// <summary>Members left out of the census for missing a required auth field (e.g. no password).</summary>
+    public List<string>? MissingData { get; set; }
 }
 
 /// <summary>GET /processes/{id} — fully hydrated; questions carry upstreamId + status after publish.</summary>
@@ -235,6 +245,8 @@ public sealed class EnqueuedResponse
     public string? JobId { get; set; }
 }
 
+public enum JobState { Completed, Failed, Pending }
+
 /// <summary>db.JobStatus of an async transaction job (publish, status change, vote relay).</summary>
 public sealed class JobStatusResponse
 {
@@ -262,7 +274,40 @@ public sealed class UpdateQuestionCensusResponse
     public int Removed { get; set; }
 }
 
-// --- integrator ------------------------------------------------------------
+public sealed class SetProcessEndDateRequest
+{
+    /// <summary>RFC 3339.</summary>
+    public string EndDate { get; set; } = "";
+}
+
+// --- pricing + integrator wallet (saas-backend #819) ---------------------------
+
+/// <summary>GET /pricing — EUR cents, VAT excluded. totalCents 0 with quoteRequired is "no price", not free.</summary>
+public sealed class ProcessPrice
+{
+    public int TotalCents { get; set; }
+    /// <summary>Lowercase ISO code, e.g. "eur".</summary>
+    public string? Currency { get; set; }
+    public List<PriceLine>? Lines { get; set; }
+    public bool QuoteRecommended { get; set; }
+    public bool QuoteRequired { get; set; }
+}
+
+public sealed class PriceLine
+{
+    public string? Kind { get; set; }
+    public string? Description { get; set; }
+    public int AmountCents { get; set; }
+}
+
+/// <summary>GET /integrator.</summary>
+public sealed class IntegratorInfo
+{
+    public bool Enabled { get; set; }
+    public bool CanCreateManagedOrg { get; set; }
+    public int ManagedOrgsCreated { get; set; }
+    public long NetTopUpCents { get; set; }
+}
 
 /// <summary>Subset of GET /integrator/organizations (paginated) we read.</summary>
 public sealed class ManagedOrganizationsResponse
@@ -274,4 +319,13 @@ public sealed class ManagedOrganizationsResponse
 public sealed class ManagedOrganizationInfo
 {
     public string? Address { get; set; }
+    /// <summary>The integrator org that manages this one (hex).</summary>
+    public string? ManagedBy { get; set; }
+}
+
+/// <summary>GET /organizations/{org}/wallet — the balance part.</summary>
+public sealed class WalletInfo
+{
+    public long BalanceCents { get; set; }
+    public string? Currency { get; set; }
 }
diff --git a/tests/HoaVoting.Tests/FakeVocdoniClient.cs b/tests/HoaVoting.Tests/FakeVocdoniClient.cs
index bfe0a42..5bcc8bf 100644
--- a/tests/HoaVoting.Tests/FakeVocdoniClient.cs
+++ b/tests/HoaVoting.Tests/FakeVocdoniClient.cs
@@ -15,9 +15,21 @@ public sealed class FakeVocdoniClient : IVocdoniClient
     public CreateVotingProcessRequest? LastCreate { get; private set; }
     public List<VocdoniOrgMember>? LastAdded { get; private set; }
 
-    public Func<CreateVotingProcessRequest, string> OnCreateProcess { get; set; } = _ => "proc1";
-    public Action<string> OnPublish { get; set; } = _ => { };
+    public VocdoniOrgMember? LastUpsert { get; private set; }
+    public DateTimeOffset? LastEndDate { get; private set; }
+
+    public Func<CreateVotingProcessRequest, CreateVotingProcessResponse> OnCreateProcess { get; set; } = _ => new() { ProcessId = "proc1" };
+    /// <summary>Publish answer: a job id (202) or null (200, already published). Throw to refuse.</summary>
+    public Func<string, string?> OnPublish { get; set; } = _ => "job1";
+    public Action<string> OnDeleteProcess { get; set; } = _ => { };
     public Func<string, VotingProcessResponse> OnGetProcess { get; set; } = id => new() { Id = id };
+    public Func<string, (JobState, string?)> OnWaitForJob { get; set; } = _ => (JobState.Completed, null);
+    public Func<string, JobStatusResponse> OnGetJob { get; set; } = _ => new() { Status = "pending" };
+    public Func<string, string> OnSetEndDate { get; set; } = _ => "job-end";
+    public Func<int, ProcessPrice> OnGetPrice { get; set; } = _ => new() { Currency = "eur" };
+    public Func<IntegratorInfo> OnGetIntegrator { get; set; } = () => new() { Enabled = true, CanCreateManagedOrg = true };
+    public Func<string?> OnGetIntegratorAddress { get; set; } = () => "0xintegrator";
+    public Func<string, WalletInfo> OnGetWallet { get; set; } = _ => new() { BalanceCents = 0, Currency = "eur" };
 
     public Task<string> CreateOrganizationAsync(string name, CancellationToken ct = default)
     {
@@ -50,17 +62,30 @@ public sealed class FakeVocdoniClient : IVocdoniClient
         return Task.CompletedTask;
     }
 
-    public Task<string> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
+    public Task UpsertMemberAsync(string orgAddress, VocdoniOrgMember member, CancellationToken ct = default)
+    {
+        Calls.Add($"UpsertMember {member.Id}");
+        LastUpsert = member;
+        return Task.CompletedTask;
+    }
+
+    public Task<CreateVotingProcessResponse> CreateVotingProcessAsync(CreateVotingProcessRequest request, CancellationToken ct = default)
     {
         Calls.Add("CreateProcess");
         LastCreate = request;
         return Task.FromResult(OnCreateProcess(request));
     }
 
-    public Task PublishVotingProcessAsync(string processId, CancellationToken ct = default)
+    public Task<string?> PublishVotingProcessAsync(string processId, CancellationToken ct = default)
     {
         Calls.Add($"Publish {processId}");
-        OnPublish(processId);
+        return Task.FromResult(OnPublish(processId));
+    }
+
+    public Task DeleteVotingProcessAsync(string processId, CancellationToken ct = default)
+    {
+        Calls.Add($"DeleteProcess {processId}");
+        OnDeleteProcess(processId);
         return Task.CompletedTask;
     }
 
@@ -81,4 +106,47 @@ public sealed class FakeVocdoniClient : IVocdoniClient
         Calls.Add($"SetQuestionCensus {processId} {questionId}");
         return Task.FromResult(new UpdateQuestionCensusResponse());
     }
+
+    public Task<string> SetProcessEndDateAsync(string processId, DateTimeOffset endDate, CancellationToken ct = default)
+    {
+        Calls.Add($"SetEndDate {processId}");
+        LastEndDate = endDate;
+        return Task.FromResult(OnSetEndDate(processId));
+    }
+
+    public Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken ct = default)
+    {
+        Calls.Add($"GetJob {jobId}");
+        return Task.FromResult(OnGetJob(jobId));
+    }
+
+    public Task<(JobState State, string? Error)> WaitForJobAsync(string jobId, CancellationToken ct = default)
+    {
+        Calls.Add($"WaitForJob {jobId}");
+        return Task.FromResult(OnWaitForJob(jobId));
+    }
+
+    public Task<ProcessPrice> GetPriceAsync(int voters, CancellationToken ct = default)
+    {
+        Calls.Add($"GetPrice {voters}");
+        return Task.FromResult(OnGetPrice(voters));
+    }
+
+    public Task<IntegratorInfo> GetIntegratorInfoAsync(CancellationToken ct = default)
+    {
+        Calls.Add("GetIntegrator");
+        return Task.FromResult(OnGetIntegrator());
+    }
+
+    public Task<string?> GetIntegratorAddressAsync(CancellationToken ct = default)
+    {
+        Calls.Add("GetIntegratorAddress");
+        return Task.FromResult(OnGetIntegratorAddress());
+    }
+
+    public Task<WalletInfo> GetWalletAsync(string orgAddress, CancellationToken ct = default)
+    {
+        Calls.Add($"GetWallet {orgAddress}");
+        return Task.FromResult(OnGetWallet(orgAddress));
+    }
 }
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: `Passed!  - Failed:     0, Passed:    42, Skipped:     0, Total:    42`

Step 3: commit
`git -c commit.gpgsign=false commit -m "feat(vocdoni): add job, delete, end-date, pricing, integrator and member upsert calls" -- src/HoaVoting.Api/Services/Vocdoni/VocdoniClient.cs src/HoaVoting.Api/Services/Vocdoni/IVocdoniClient.cs src/HoaVoting.Api/Services/Vocdoni/VocdoniModels.cs src/HoaVoting.Api/Controllers/ProposalsController.cs tests/HoaVoting.Tests/VocdoniClientTests.cs tests/HoaVoting.Tests/FakeVocdoniClient.cs`

Interfaces this task provides to later tasks (all on `IVocdoniClient`):
`Task<CreateVotingProcessResponse> CreateVotingProcessAsync(req, ct)` (`ProcessId`, `MissingData`);
`Task<string?> PublishVotingProcessAsync(processId, ct)`; `Task DeleteVotingProcessAsync(processId, ct)`;
`Task<JobStatusResponse> GetJobAsync(jobId, ct)`; `Task<(JobState State, string? Error)> WaitForJobAsync(jobId, ct)` with `enum JobState { Completed, Failed, Pending }`;
`Task<string> SetProcessEndDateAsync(processId, DateTimeOffset, ct)`; `Task<ProcessPrice> GetPriceAsync(int voters, ct)`;
`Task<IntegratorInfo> GetIntegratorInfoAsync(ct)`; `Task<string?> GetIntegratorAddressAsync(ct)`; `Task<WalletInfo> GetWalletAsync(org, ct)`;
`Task UpsertMemberAsync(org, VocdoniOrgMember, ct)`. Fake: `OnPublish` (`string? job`, throw to refuse), `OnDeleteProcess`, `OnWaitForJob`, `OnGetJob`, `OnSetEndDate`, `OnGetPrice`, `OnGetIntegrator`, `OnGetIntegratorAddress`, `OnGetWallet`, `LastUpsert`, `LastEndDate`.

### Task 4: EF model and migration
Model: fast   Repo: same   Files: src/HoaVoting.Api/Models/Proposal.cs, src/HoaVoting.Api/Migrations/<timestamp>_PublishStateAndPassword.cs, src/HoaVoting.Api/Migrations/<timestamp>_PublishStateAndPassword.Designer.cs, src/HoaVoting.Api/Migrations/AppDbContextModelSnapshot.cs, tests/HoaVoting.Tests/MigrationTests.cs
Goal: spec Compatibility — `ProposalStatus.Failed` appended (int 3; enum at `Models/Proposal.cs:3-8`), `Proposal.RequirePassword` (bool, default false), `PublishJobId` and `PublishError` (string?, null). One generated migration.

Step 1: write the failing test, and make the model change (the test needs the new members to compile). `Migrate()` refuses a model with changes no migration captures.

`tests/HoaVoting.Tests/MigrationTests.cs` (new file, full content):

````csharp
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
````

````diff
diff --git a/src/HoaVoting.Api/Models/Proposal.cs b/src/HoaVoting.Api/Models/Proposal.cs
index a4da845..2a3b403 100644
--- a/src/HoaVoting.Api/Models/Proposal.cs
+++ b/src/HoaVoting.Api/Models/Proposal.cs
@@ -1,10 +1,13 @@
 namespace HoaVoting.Api.Models;
 
+// Stored as int: append only. Draft = created upstream, publish not confirmed yet; Failed = the
+// publish was refused or its job failed (PublishError says why) — retry or delete it.
 public enum ProposalStatus
 {
     Draft,
     Open,
     Closed,
+    Failed,
 }
 
 /// <summary>
@@ -32,6 +35,16 @@ public class Proposal
     /// Set at creation; the public voting page uses it to pick the blind-signature flow.</summary>
     public bool Anonymous { get; set; }
 
+    /// <summary>Census auth also needs the member's password (saas-backend #760); members without one
+    /// are left out of the census at creation.</summary>
+    public bool RequirePassword { get; set; }
+
+    /// <summary>Upstream publish job, stored as soon as it is enqueued so the reconcile can follow it.</summary>
+    public string? PublishJobId { get; set; }
+
+    /// <summary>Why the publish failed (readable upstream text); set only while Status is Failed.</summary>
+    public string? PublishError { get; set; }
+
     public List<ProposalQuestion> Questions { get; set; } = new();
 
     public DateTimeOffset StartDate { get; set; }
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test --filter MigrationTests`
Expected: `System.InvalidOperationException : An error was generated for warning 'Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning': The model for context 'AppDbContext' has pending changes.`

Step 2: generate the migration — do not write or edit it by hand:
```bash
docker run --rm -v "$PWD":/src -w /src/src/HoaVoting.Api mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -lc 'dotnet tool install -g dotnet-ef >/dev/null 2>&1; export PATH=$PATH:/root/.dotnet/tools; \
            dotnet ef migrations add PublishStateAndPassword'
```
Expected: `Done. To undo this action, use 'ef migrations remove'`, and `git status` shows two new
`src/HoaVoting.Api/Migrations/<timestamp>_PublishStateAndPassword{,.Designer}.cs` plus a modified
`AppDbContextModelSnapshot.cs`. The `Up()` must be exactly three `AddColumn`s on `Proposals`:
`PublishError` (TEXT, nullable), `PublishJobId` (TEXT, nullable), `RequirePassword` (INTEGER, not
null, `defaultValue: false`); the snapshot diff adds those three properties (and bumps
`ProductVersion` 10.0.9 → 10.0.11, expected). Anything else (drops, renames, other tables) means the
model change is wrong: `dotnet ef migrations remove` in the same container and fix the model.

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: `Passed!  - Failed:     0, Passed:    43, Skipped:     0, Total:    43` in the default order (if run in parallel on another base: that base's count + 1)

Step 3: commit (use the generated file names)
`git add src/HoaVoting.Api/Migrations/*_PublishStateAndPassword*.cs tests/HoaVoting.Tests/MigrationTests.cs && git -c commit.gpgsign=false commit -m "feat(api): store publish state and password requirement on proposals" -- src/HoaVoting.Api/Models/Proposal.cs src/HoaVoting.Api/Migrations/*_PublishStateAndPassword*.cs src/HoaVoting.Api/Migrations/AppDbContextModelSnapshot.cs tests/HoaVoting.Tests/MigrationTests.cs`

Interfaces this task provides to later tasks: `ProposalStatus { Draft, Open, Closed, Failed }`; `Proposal.RequirePassword`, `Proposal.PublishJobId`, `Proposal.PublishError`.

### Task 5: Resilient create, shared reconcile, delete/retry/close-409, public 404, end-date endpoint
Model: strong   Repo: same   Files: src/HoaVoting.Api/Services/ProposalSync.cs, src/HoaVoting.Api/Controllers/ProposalsController.cs, src/HoaVoting.Api/Controllers/VotingController.cs, src/HoaVoting.Api/Dtos/Dtos.cs, tests/HoaVoting.Tests/ProposalLifecycleTests.cs
Goal: spec C and E, the `RequirePassword` part of F (census `authFields`, persisted, on `ProposalResponse` and on `VotingInfoResponse` with `OrgAddress`); AC2, AC6, the RequirePassword clause of AC7. Replaces `Create` (`ProposalsController.cs:24-93`: validate ≤ 15 questions and ≤ 90 days, save `Draft` before publishing, then `ProposalSync.PublishAsync`), `HydrateAsync` (`:218-250`) with `ProposalSync.ReconcileAsync` + `SaveAsync`, adds a 409 to `Close` (`:131-135`), adds `DELETE {id}`, `POST {id}/publish`, `PUT {id}/end-date`; `VotingController.Get` (`:21-57`) runs the same reconcile, saves, and answers 404 for `Draft`/`Failed`. `IsEnded` (`:252`) moves to `ProposalSync`. DTOs: `CreateProposalRequest` (`Dtos.cs:26-33`) + `RequirePassword`; `ProposalResponse` (`:48-59`) + `RequirePassword`, `PublishError`, `MissingData`; `VotingInfoResponse` (`:86-99`) + `RequirePassword`, `OrgAddress`; new `SetEndDateRequest(DateTimeOffset EndDate)`.

Step 1: write the failing test

`tests/HoaVoting.Tests/ProposalLifecycleTests.cs` (new file, full content):

````csharp
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
    public async Task Create_already_published_skips_the_job()
    {
        Vocdoni.OnPublish = _ => null; // 200: already published
        Vocdoni.OnGetProcess = id => PublishedProcess(id, "ready", "ready");

        var res = await Proposals().Create(Assoc.Id, Request(), default);

        Assert.Equal(201, StatusOf(res));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("WaitForJob"));
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
        Assert.Equal(ProposalStatus.Draft, Saved(p.Id).Status);
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
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: build errors, e.g. `error CS1739: The best overload for 'CreateProposalRequest' does not have a parameter named 'RequirePassword'` and `error CS1061: 'ProposalsController' does not contain a definition for 'Delete'`

Step 2: implement

`src/HoaVoting.Api/Services/ProposalSync.cs` (new file, full content):

````csharp
using System.Globalization;
using System.Net;
using HoaVoting.Api.Data;
using HoaVoting.Api.Models;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Api.Services;

public enum PublishOutcome { Open, Pending, Failed }

/// <summary>
/// Keeps a local <see cref="Proposal"/> in step with its upstream process. Publishing is pay-per-process
/// (saas-backend #819) and its job can stay pending for hours, so the local row is the record of what
/// upstream did: it is saved before the publish call, and every save that follows an upstream side
/// effect uses <see cref="CancellationToken.None"/> so a client disconnect cannot drop it.
/// </summary>
internal static class ProposalSync
{
    /// <summary>
    /// The publish routine (create and retry): publish, follow the job for the bounded poll, read back.
    /// A refusal deletes the upstream draft when <paramref name="deleteOnRefusal"/> (create) and the row
    /// with it; otherwise, or when that delete fails, the row turns Failed. Either way the refusal is
    /// rethrown (→ 502 with the readable detail). No answer at all (transport error, timeout, cancel)
    /// propagates with the row still Draft and no job id: the reconcile settles it from the process read.
    /// </summary>
    public static async Task<PublishOutcome> PublishAsync(
        AppDbContext db, IVocdoniClient vocdoni, Proposal p, bool deleteOnRefusal, CancellationToken ct)
    {
        string? jobId;
        try
        {
            jobId = await vocdoni.PublishVotingProcessAsync(p.VocdoniProcessId, ct);
        }
        catch (VocdoniApiException e)
        {
            if (deleteOnRefusal && await TryDeleteUpstreamAsync(vocdoni, p.VocdoniProcessId))
                db.Proposals.Remove(p);
            else
                MarkFailed(p, UpstreamErrors.Describe(e).Detail);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        if (jobId is not null)
        {
            p.PublishJobId = jobId;
            await db.SaveChangesAsync(CancellationToken.None);

            (JobState State, string? Error) job;
            try
            {
                job = await vocdoni.WaitForJobAsync(jobId, ct);
            }
            catch (Exception e) when (e is VocdoniApiException or HttpRequestException)
            {
                return PublishOutcome.Pending; // job id is stored; the reconcile follows it
            }

            if (job.State == JobState.Pending) return PublishOutcome.Pending;
            if (job.State == JobState.Failed)
            {
                // Never auto-delete here: the job may have submitted elections, and upstream refuses to
                // delete such a draft (409 40903/40904). Retry publish adopts them at no new charge.
                MarkFailed(p, string.IsNullOrEmpty(job.Error) ? "The publish job failed." : job.Error);
                await db.SaveChangesAsync(CancellationToken.None);
                return PublishOutcome.Failed;
            }
        }

        MarkOpen(p, await vocdoni.GetVotingProcessAsync(p.VocdoniProcessId, ct));
        await db.SaveChangesAsync(CancellationToken.None);
        return PublishOutcome.Open;
    }

    /// <summary>
    /// Read-side reconcile of one proposal, in order: settle a Draft (promote from a process read that
    /// proves the publish landed, or fail it from its job), copy upstream's end date, refresh question
    /// status, then close an Open proposal whose questions all ended or whose end date passed. It only
    /// promotes or fails rows, never deletes. Returns the upstream questions keyed by on-chain id (tallies).
    /// The caller saves (<see cref="SaveAsync"/>).
    /// </summary>
    public static async Task<Dictionary<string, VotingProcessQuestion>> ReconcileAsync(
        IVocdoniClient vocdoni, Proposal p, CancellationToken ct)
    {
        VotingProcessResponse? proc = null;
        try
        {
            proc = await vocdoni.GetVotingProcessAsync(p.VocdoniProcessId, ct);
        }
        catch (Exception e) when (e is VocdoniApiException or HttpRequestException) { /* serve stored values */ }

        // 1. Draft: only a read that says published, with an upstream id for every local question, promotes.
        if (p.Status == ProposalStatus.Draft)
        {
            if (proc is not null && IsFullyPublished(p, proc))
                MarkOpen(p, proc);
            else if (p.PublishJobId is { } jobId)
            {
                try
                {
                    var job = await vocdoni.GetJobAsync(jobId, ct);
                    if (job.Status == "failed") MarkFailed(p, string.Join("; ", job.Errors ?? []));
                }
                catch (Exception e) when (e is VocdoniApiException or HttpRequestException) { /* 404 / unreachable: stay Draft */ }
            }
        }

        var byUpstream = new Dictionary<string, VotingProcessQuestion>();
        if (proc is not null)
        {
            // 2. End date: upstream wins, so an extension whose job outlived its request shows up here.
            if (DateTimeOffset.TryParse(proc.EndDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
                && end != p.EndDate)
                p.EndDate = end;

            // 3. Question status.
            foreach (var q in proc.Questions)
                if (!string.IsNullOrEmpty(q.UpstreamId)) byUpstream[q.UpstreamId!] = q;
            foreach (var q in p.Questions)
                if (q.UpstreamId != "" && byUpstream.TryGetValue(q.UpstreamId, out var pq) && !string.IsNullOrEmpty(pq.Status))
                    q.Status = pq.Status!;
        }

        // 4. Closed: Open proposals only — a Draft or Failed one past its end date stays resolvable.
        if (p.Status == ProposalStatus.Open
            && ((p.Questions.Count > 0 && p.Questions.All(q => IsEnded(q.Status))) || p.EndDate < DateTimeOffset.UtcNow))
            p.Status = ProposalStatus.Closed;

        return byUpstream;
    }

    /// <summary>Saves what the reconcile changed; a row deleted meanwhile (create cleanup, DELETE) is not an error.</summary>
    public static async Task SaveAsync(AppDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException) { /* serve what we read; the next read is consistent */ }
    }

    private static bool IsFullyPublished(Proposal p, VotingProcessResponse proc) =>
        proc.Published && p.Questions.All(q => !string.IsNullOrEmpty(proc.Questions.ElementAtOrDefault(q.Order)?.UpstreamId));

    // Questions are matched to the process read by order (the order they were authored in).
    private static void MarkOpen(Proposal p, VotingProcessResponse proc)
    {
        foreach (var q in p.Questions)
        {
            var pq = proc.Questions.ElementAtOrDefault(q.Order);
            q.UpstreamId = pq?.UpstreamId ?? "";
            q.Status = pq?.Status ?? "";
        }
        p.ChainId = proc.ChainId ?? "";
        p.Status = ProposalStatus.Open;
        p.PublishError = null;
    }

    private static void MarkFailed(Proposal p, string error)
    {
        p.Status = ProposalStatus.Failed;
        p.PublishError = error;
    }

    // Cleanup after a refused create: true when the draft is gone upstream (deleted, or already 404).
    // CancellationToken.None: a disconnect must not leave an orphan draft behind.
    private static async Task<bool> TryDeleteUpstreamAsync(IVocdoniClient vocdoni, string processId)
    {
        try
        {
            await vocdoni.DeleteVotingProcessAsync(processId, CancellationToken.None);
            return true;
        }
        catch (VocdoniApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (Exception e) when (e is VocdoniApiException or HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    public static bool IsEnded(string status) => status.ToUpperInvariant() is "ENDED" or "CANCELED" or "RESULTS";
}
````

````diff
diff --git a/src/HoaVoting.Api/Controllers/ProposalsController.cs b/src/HoaVoting.Api/Controllers/ProposalsController.cs
index 49f66f5..ed955e1 100644
--- a/src/HoaVoting.Api/Controllers/ProposalsController.cs
+++ b/src/HoaVoting.Api/Controllers/ProposalsController.cs
@@ -1,8 +1,10 @@
+using System.Net;
 using System.Text.Json;
 using HoaVoting.Api.Authorization;
 using HoaVoting.Api.Data;
 using HoaVoting.Api.Dtos;
 using HoaVoting.Api.Models;
+using HoaVoting.Api.Services;
 using HoaVoting.Api.Services.Vocdoni;
 using Microsoft.AspNetCore.Authorization;
 using Microsoft.AspNetCore.Mvc;
@@ -14,11 +16,15 @@ namespace HoaVoting.Api.Controllers;
 [Route("api/associations/{associationId:int}/proposals")]
 public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiControllerBase
 {
+    private const int MaxQuestions = 15;
+    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(90);
+
     /// <summary>
     /// Create a proposal as a Vocdoni multi-question voting process (saas-backend #571): author the
-    /// process (census inline over the current homeowners, one election per question), publish it
-    /// (async batch), then read it back to capture each question's on-chain election id + status.
-    /// Voters cast per question client-side.
+    /// process (census inline over the current homeowners, one election per question), save the local
+    /// row as Draft, then run the publish routine (<see cref="ProposalSync.PublishAsync"/>). 201 + Open
+    /// when the publish job finished within the bounded poll, 202 + Draft while it is still running (the
+    /// reconcile promotes it later), 502 when upstream refused or the job failed.
     /// </summary>
     [HttpPost]
     public async Task<ActionResult<ProposalResponse>> Create(int associationId, CreateProposalRequest req, CancellationToken ct)
@@ -26,6 +32,9 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
         var (assoc, error) = await ResolveAsync(associationId, ct);
         if (error is not null) return error;
         if (req.Questions is null || req.Questions.Count == 0) return BadRequest("A proposal needs at least one question.");
+        // Upstream limits (saas-backend #819): 15 questions per process, 90-day processes.
+        if (req.Questions.Count > MaxQuestions) return BadRequest($"A proposal can have at most {MaxQuestions} questions.");
+        if (req.EndDate - req.StartDate > MaxDuration) return BadRequest($"A proposal can run for at most {MaxDuration.TotalDays} days.");
         if (req.Questions.Any(q => q.Choices.Count < 2)) return BadRequest("Each question needs at least two choices.");
         // An open "Other" choice (free-text voter memo, #577) is single-choice-only, at most one per question.
         foreach (var (q, qi) in req.Questions.Select((q, i) => (q, i)))
@@ -46,9 +55,15 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
         var request = new CreateVotingProcessRequest
         {
             OrgAddress = org,
-            // Auth-only census over the current homeowners (authenticate by member number, no 2FA).
+            // Auth-only census over the current homeowners (member number, plus the member's password when
+            // required — saas-backend #760; members without one land in missingData). No 2FA.
             // Anonymous (saas-backend #641) swaps the CSP to blind signatures; omitted when false.
-            Census = new CensusSpec { AuthFields = ["memberNumber"], MemberIds = memberIds, Anonymous = req.Anonymous ? true : null },
+            Census = new CensusSpec
+            {
+                AuthFields = req.RequirePassword ? ["memberNumber", "password"] : ["memberNumber"],
+                MemberIds = memberIds,
+                Anonymous = req.Anonymous ? true : null,
+            },
             Title = Lang(req.Title),
             Description = Lang(req.Description),
             StartDate = req.StartDate.UtcDateTime.ToString("o"),
@@ -56,25 +71,21 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
             Questions = req.Questions.Select(ToQuestionRequest).ToList(),
         };
 
-        var processId = (await vocdoni.CreateVotingProcessAsync(request, ct)).ProcessId!;
-        // Interim (Task 5 replaces this with the publish routine): today's wait-then-read behaviour.
-        if (await vocdoni.PublishVotingProcessAsync(processId, ct) is { } jobId
-            && await vocdoni.WaitForJobAsync(jobId, ct) is var (state, jobError) && state != JobState.Completed)
-            throw new VocdoniApiException(System.Net.HttpStatusCode.BadGateway, $"publish job {state}: {jobError}");
-        // Read back to capture each question's on-chain id + status (assigned during the batch publish).
-        var published = await vocdoni.GetVotingProcessAsync(processId, ct);
+        var created = await vocdoni.CreateVotingProcessAsync(request, ct);
 
+        // From here on upstream holds a draft: record it before publishing (and whatever happens next)
+        // with CancellationToken.None, so a client disconnect cannot leave it untracked.
         var proposal = new Proposal
         {
             AssociationId = assoc.Id,
             Title = req.Title,
             Description = req.Description,
-            VocdoniProcessId = processId,
-            ChainId = published.ChainId ?? "",
+            VocdoniProcessId = created.ProcessId!,
             Anonymous = req.Anonymous,
+            RequirePassword = req.RequirePassword,
             StartDate = req.StartDate,
             EndDate = req.EndDate,
-            Status = ProposalStatus.Open,
+            Status = ProposalStatus.Draft,
             CreatedAt = DateTimeOffset.UtcNow,
             Questions = req.Questions.Select((q, i) => new ProposalQuestion
             {
@@ -85,14 +96,19 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
                 Kind = q.Kind,
                 Budget = q.Kind == VotingType.Cumulative ? q.Budget : null,
                 CostExponent = q.Kind == VotingType.Cumulative ? q.CostExponent : null,
-                UpstreamId = published.Questions.ElementAtOrDefault(i)?.UpstreamId ?? "",
-                Status = published.Questions.ElementAtOrDefault(i)?.Status ?? "",
             }).ToList(),
         };
         db.Proposals.Add(proposal);
-        await db.SaveChangesAsync(ct);
+        await db.SaveChangesAsync(CancellationToken.None);
 
-        return CreatedAtAction(nameof(Get), new { associationId, id = proposal.Id }, ToResponse(proposal));
+        var outcome = await ProposalSync.PublishAsync(db, vocdoni, proposal, deleteOnRefusal: true, ct);
+        var body = ToResponse(proposal, missingData: created.MissingData);
+        return outcome switch
+        {
+            PublishOutcome.Open => CreatedAtAction(nameof(Get), new { associationId, id = proposal.Id }, body),
+            PublishOutcome.Pending => Accepted(body),
+            _ => UpstreamFailure(proposal.PublishError!),
+        };
     }
 
     [HttpGet]
@@ -113,17 +129,71 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
         var (_, error) = await ResolveAsync(associationId, ct);
         if (error is not null) return error;
 
-        var p = await db.Proposals.Include(x => x.Questions)
-            .SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
+        var p = await FindAsync(associationId, id, ct);
         if (p is null) return NotFound();
         var results = await HydrateAsync([p], ct);
         return ToResponse(p, results.GetValueOrDefault(p.Id));
     }
 
+    /// <summary>
+    /// Delete a proposal stuck publishing (Draft) or whose publish failed (Failed): upstream draft first
+    /// (200 or 404 → the local row goes too, 204). Any other upstream answer — typically 409 because a
+    /// publish submitted elections — propagates as 502 and the row stays (Retry publish resolves those).
+    /// </summary>
+    [HttpDelete("{id:int}")]
+    public async Task<ActionResult> Delete(int associationId, int id, CancellationToken ct)
+    {
+        var (_, error) = await ResolveAsync(associationId, ct);
+        if (error is not null) return error;
+
+        var p = await FindAsync(associationId, id, ct);
+        if (p is null) return NotFound();
+        if (p.Status is not (ProposalStatus.Draft or ProposalStatus.Failed))
+            return Conflict("Only a proposal that is still publishing or failed to publish can be deleted.");
+
+        try
+        {
+            await vocdoni.DeleteVotingProcessAsync(p.VocdoniProcessId, ct);
+        }
+        catch (VocdoniApiException e) when (e.Status == HttpStatusCode.NotFound) { /* already gone upstream */ }
+
+        db.Proposals.Remove(p);
+        await db.SaveChangesAsync(CancellationToken.None);
+        return NoContent();
+    }
+
+    /// <summary>
+    /// Retry the publish of a Failed proposal. Upstream adopts any election a previous attempt already
+    /// mined, at no new charge. Same routine as create, except a refusal marks it Failed again (no delete).
+    /// </summary>
+    [HttpPost("{id:int}/publish")]
+    public async Task<ActionResult<ProposalResponse>> Publish(int associationId, int id, CancellationToken ct)
+    {
+        var (_, error) = await ResolveAsync(associationId, ct);
+        if (error is not null) return error;
+
+        var p = await FindAsync(associationId, id, ct);
+        if (p is null) return NotFound();
+        if (p.Status != ProposalStatus.Failed) return Conflict("Only a proposal whose publish failed can be published again.");
+
+        // Drop the old job too: the reconcile must not fail the retry from the previous attempt's job.
+        p.Status = ProposalStatus.Draft;
+        p.PublishError = null;
+        p.PublishJobId = null;
+        await db.SaveChangesAsync(CancellationToken.None);
+
+        return await ProposalSync.PublishAsync(db, vocdoni, p, deleteOnRefusal: false, ct) switch
+        {
+            PublishOutcome.Open => ToResponse(p),
+            PublishOutcome.Pending => Accepted(ToResponse(p)),
+            _ => UpstreamFailure(p.PublishError!),
+        };
+    }
+
     /// <summary>
     /// Close voting: enqueue the on-chain end of every question and return immediately (the change is
     /// async). We don't optimistically mark the proposal Closed — the real status is reconciled from
-    /// GET /processes/{id}/results by HydrateAsync once the end tx mines and the questions report ended.
+    /// the process read once the end tx mines and the questions report ended. Open proposals only.
     /// </summary>
     [HttpPost("{id:int}/close")]
     public async Task<ActionResult> Close(int associationId, int id, CancellationToken ct)
@@ -133,11 +203,43 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
 
         var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
         if (p is null) return NotFound();
+        if (p.Status != ProposalStatus.Open) return Conflict("Only an open proposal can be closed.");
 
         await vocdoni.SetQuestionsStatusAsync(p.VocdoniProcessId, "ended", null, ct);
         return Accepted();
     }
 
+    /// <summary>
+    /// Extend an open proposal's end date (saas-backend #844). 200 with the new end once the job
+    /// completed; 202 with the old end while it is still running (the reconcile copies upstream's end
+    /// date later); 502 when it failed. Upstream enforces the 5-minute / 90-day rules (400 → 502).
+    /// </summary>
+    [HttpPut("{id:int}/end-date")]
+    public async Task<ActionResult<ProposalResponse>> SetEndDate(int associationId, int id, SetEndDateRequest req, CancellationToken ct)
+    {
+        var (_, error) = await ResolveAsync(associationId, ct);
+        if (error is not null) return error;
+
+        var p = await FindAsync(associationId, id, ct);
+        if (p is null) return NotFound();
+        if (p.Status != ProposalStatus.Open) return BadRequest("Only an open proposal's end date can be extended.");
+        if (req.EndDate <= p.EndDate) return BadRequest("The new end date must be later than the current one.");
+
+        var jobId = await vocdoni.SetProcessEndDateAsync(p.VocdoniProcessId, req.EndDate, ct);
+        var (state, jobError) = await vocdoni.WaitForJobAsync(jobId, ct);
+        switch (state)
+        {
+            case JobState.Completed:
+                p.EndDate = req.EndDate;
+                await db.SaveChangesAsync(CancellationToken.None);
+                return ToResponse(p);
+            case JobState.Pending:
+                return Accepted(ToResponse(p));
+            default:
+                return UpstreamFailure(string.IsNullOrEmpty(jobError) ? "The end-date change failed." : jobError);
+        }
+    }
+
     /// <summary>
     /// Replace a question's voter-eligibility restriction on a live process (saas-backend #621).
     /// The body carries the COMPLETE desired member-id list; [] reopens the question to the whole
@@ -214,46 +316,19 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
         }
     }
 
-    // Refresh each proposal's questions from the process read GET /processes/{id} (saas-backend #596:
-    // per-question live status always, plus an inline tally once the question hits "results"), mark the
-    // proposal Closed once every question has ended (or its end date passed), and return each upstream
-    // question (tally + eligibility, #621) keyed by on-chain id for the response. Best-effort per proposal.
+    // Reconcile each proposal with its process read (ProposalSync.ReconcileAsync: settle drafts, end date,
+    // question status, Closed) and return each upstream question (tally + eligibility, #621) keyed by
+    // on-chain id for the response. Best-effort per proposal; one save for the batch.
     private async Task<Dictionary<int, Dictionary<string, VotingProcessQuestion>>> HydrateAsync(
         IReadOnlyList<Proposal> proposals, CancellationToken ct)
     {
         var map = new Dictionary<int, Dictionary<string, VotingProcessQuestion>>();
-        var changed = false;
         foreach (var p in proposals)
-        {
-            var byUpstream = new Dictionary<string, VotingProcessQuestion>();
-            try
-            {
-                var proc = await vocdoni.GetVotingProcessAsync(p.VocdoniProcessId, ct);
-                foreach (var q in proc.Questions)
-                    if (!string.IsNullOrEmpty(q.UpstreamId)) byUpstream[q.UpstreamId!] = q;
-            }
-            catch (VocdoniApiException) { /* not published yet / unreachable — serve stored values */ }
-            catch (HttpRequestException) { }
-
-            foreach (var q in p.Questions)
-            {
-                if (string.IsNullOrEmpty(q.UpstreamId) || !byUpstream.TryGetValue(q.UpstreamId, out var pq)) continue;
-                if (!string.IsNullOrEmpty(pq.Status) && pq.Status != q.Status) { q.Status = pq.Status!; changed = true; }
-            }
-            map[p.Id] = byUpstream;
-
-            if (p.Status != ProposalStatus.Closed)
-            {
-                var ended = (p.Questions.Count > 0 && p.Questions.All(q => IsEnded(q.Status))) || p.EndDate < DateTimeOffset.UtcNow;
-                if (ended) { p.Status = ProposalStatus.Closed; changed = true; }
-            }
-        }
-        if (changed) await db.SaveChangesAsync(ct);
+            map[p.Id] = await ProposalSync.ReconcileAsync(vocdoni, p, ct);
+        await ProposalSync.SaveAsync(db);
         return map;
     }
 
-    private static bool IsEnded(string status) => status.ToUpperInvariant() is "ENDED" or "CANCELED" or "RESULTS";
-
     // Map the demo's UI kind to a #571 question, using the named types only (saas-backend #638).
     // The backend derives the on-chain ballot protocol from type + typeSetup; supplying a protocol that
     // contradicts the named type is a 400, and ranked rejects any typeSetup at all (choices define it).
@@ -288,7 +363,8 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
 
     private static Dictionary<string, string> Lang(string text) => new() { ["default"] = text };
 
-    private static ProposalResponse ToResponse(Proposal p, IReadOnlyDictionary<string, VotingProcessQuestion>? upstream = null)
+    private static ProposalResponse ToResponse(
+        Proposal p, IReadOnlyDictionary<string, VotingProcessQuestion>? upstream = null, List<string>? missingData = null)
     {
         var questions = p.Questions.OrderBy(q => q.Order).Select(q =>
         {
@@ -304,9 +380,17 @@ public class ProposalsController(AppDbContext db, IVocdoniClient vocdoni) : ApiC
         }).ToList();
         return new ProposalResponse(
             p.Id, p.AssociationId, p.Title, p.Description,
-            p.Status.ToString(), p.VocdoniProcessId, p.StartDate, p.EndDate, p.CreatedAt, questions, p.Anonymous);
+            p.Status.ToString(), p.VocdoniProcessId, p.StartDate, p.EndDate, p.CreatedAt, questions, p.Anonymous,
+            p.RequirePassword, p.PublishError, missingData);
     }
 
+    // Same body shape as Program.cs's mapping of a thrown upstream failure.
+    private ObjectResult UpstreamFailure(string detail) =>
+        StatusCode(StatusCodes.Status502BadGateway, new { error = "vocdoni_upstream", detail });
+
+    private Task<Proposal?> FindAsync(int associationId, int id, CancellationToken ct) =>
+        db.Proposals.Include(x => x.Questions).SingleOrDefaultAsync(x => x.Id == id && x.AssociationId == associationId, ct);
+
     private async Task<(Association?, ActionResult?)> ResolveAsync(int associationId, CancellationToken ct)
     {
         var assoc = await db.Associations.FindAsync([associationId], ct);
diff --git a/src/HoaVoting.Api/Controllers/VotingController.cs b/src/HoaVoting.Api/Controllers/VotingController.cs
index b4c04cd..21fd501 100644
--- a/src/HoaVoting.Api/Controllers/VotingController.cs
+++ b/src/HoaVoting.Api/Controllers/VotingController.cs
@@ -1,6 +1,8 @@
 using System.Text.Json;
 using HoaVoting.Api.Data;
 using HoaVoting.Api.Dtos;
+using HoaVoting.Api.Models;
+using HoaVoting.Api.Services;
 using HoaVoting.Api.Services.Vocdoni;
 using Microsoft.AspNetCore.Authorization;
 using Microsoft.AspNetCore.Mvc;
@@ -18,20 +20,15 @@ public class VotingController(AppDbContext db, IVocdoniClient vocdoni, IOptions<
     [HttpGet("{processId}")]
     public async Task<ActionResult<VotingInfoResponse>> Get(string processId, CancellationToken ct)
     {
-        var p = await db.Proposals.Include(x => x.Questions)
+        var p = await db.Proposals.Include(x => x.Questions).Include(x => x.Association)
             .SingleOrDefaultAsync(x => x.VocdoniProcessId == processId, ct);
         if (p is null) return NotFound();
 
-        // Live per-question status + inline tally (saas-backend #596), best-effort (page still renders if unreachable).
-        var byUpstream = new Dictionary<string, VotingProcessQuestion>();
-        try
-        {
-            var proc = await vocdoni.GetVotingProcessAsync(processId, ct);
-            foreach (var q in proc.Questions)
-                if (!string.IsNullOrEmpty(q.UpstreamId)) byUpstream[q.UpstreamId!] = q;
-        }
-        catch (VocdoniApiException) { /* serve stored values */ }
-        catch (HttpRequestException) { }
+        // Same reconcile as the owner reads (promotes a published Draft, copies an extended end date, live
+        // per-question status + inline tally, #596), best-effort: the page still renders if unreachable.
+        var byUpstream = await ProposalSync.ReconcileAsync(vocdoni, p, ct);
+        await ProposalSync.SaveAsync(db);
+        if (p.Status is ProposalStatus.Draft or ProposalStatus.Failed) return NotFound(); // nothing to vote on yet
 
         var opts = vocdoniOptions.Value;
         // The page calls the SaaS API from the browser, so hand it the browser-reachable URL (BrowserUrl),
@@ -47,13 +44,14 @@ public class VotingController(AppDbContext db, IVocdoniClient vocdoni, IOptions<
             return new PublicQuestion(
                 q.Id, q.Order, q.Title,
                 JsonSerializer.Deserialize<List<string>>(q.ChoicesJson) ?? [],
-                q.Kind, q.OpenChoiceIndex, q.UpstreamId, pq?.Status ?? q.Status,
+                q.Kind, q.OpenChoiceIndex, q.UpstreamId, q.Status,
                 qr?.VoteCount ?? 0, qr?.MaxVoters ?? 0, qr?.Results,
                 q.Budget, q.CostExponent);
         }).ToList();
 
         return new VotingInfoResponse(
             p.VocdoniProcessId, apiUrl, p.ChainId, p.Title, p.Description,
-            p.StartDate, p.EndDate, p.Status.ToString(), questions, p.Anonymous);
+            p.StartDate, p.EndDate, p.Status.ToString(), questions, p.Anonymous,
+            p.RequirePassword, p.Association!.VocdoniOrgAddress);
     }
 }
diff --git a/src/HoaVoting.Api/Dtos/Dtos.cs b/src/HoaVoting.Api/Dtos/Dtos.cs
index c79f673..d6cc0ba 100644
--- a/src/HoaVoting.Api/Dtos/Dtos.cs
+++ b/src/HoaVoting.Api/Dtos/Dtos.cs
@@ -30,7 +30,9 @@ public record CreateProposalRequest(
     DateTimeOffset EndDate,
     List<QuestionInput> Questions,
     // Blind-CSP anonymous voting (saas-backend #641), on every plan (#718).
-    bool Anonymous = false);
+    bool Anonymous = false,
+    // Voters also enter their password (saas-backend #760); homeowners without one are left out.
+    bool RequirePassword = false);
 
 public record QuestionInput(
     string Title,
@@ -56,7 +58,12 @@ public record ProposalResponse(
     DateTimeOffset EndDate,
     DateTimeOffset CreatedAt,
     List<QuestionResponse> Questions,
-    bool Anonymous = false);
+    bool Anonymous = false,
+    bool RequirePassword = false,
+    // Status Failed only: why the publish failed (readable upstream text).
+    string? PublishError = null,
+    // Create response only: homeowners left out of the census for missing auth data (e.g. no password).
+    List<string>? MissingData = null);
 
 public record QuestionResponse(
     int Id,
@@ -96,7 +103,11 @@ public record VotingInfoResponse(
     string Status,
     List<PublicQuestion> Questions,
     // Blind-CSP anonymous process (saas-backend #641) — the page picks the blind-signature flow.
-    bool Anonymous = false);
+    bool Anonymous = false,
+    // Password census (saas-backend #760): the page asks for it and hashes it with OrgAddress in the
+    // browser (hashMemberPassword) — the plaintext never reaches this backend.
+    bool RequirePassword = false,
+    string OrgAddress = "");
 
 public record PublicQuestion(
     int Id,
@@ -119,6 +130,8 @@ public record PublicQuestion(
     int? Budget = null,
     int? CostExponent = null);
 
+public record SetEndDateRequest(DateTimeOffset EndDate);
+
 // Live per-question eligibility (saas-backend #621). MemberIds is the COMPLETE desired list
 // (Vocdoni member ids); [] reopens the question to the whole census.
 public record SetEligibilityRequest(List<string> MemberIds);
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: `Passed!  - Failed:     0, Passed:    73, Skipped:     0, Total:    73`

Self-check before committing (both must make a test fail, then revert): replace the `catch (DbUpdateConcurrencyException)` in `ProposalSync.SaveAsync` with another exception type → `Read_survives_the_row_being_deleted_meanwhile` fails; change `if (p.Status == ProposalStatus.Open` in rule 4 to `!= ProposalStatus.Closed` → `Failed_and_Draft_past_their_end_date_are_never_closed` fails.

Step 3: commit
`git add src/HoaVoting.Api/Services/ProposalSync.cs tests/HoaVoting.Tests/ProposalLifecycleTests.cs && git -c commit.gpgsign=false commit -m "feat(api): publish proposals resiliently and reconcile drafts on read" -- src/HoaVoting.Api/Services/ProposalSync.cs src/HoaVoting.Api/Controllers/ProposalsController.cs src/HoaVoting.Api/Controllers/VotingController.cs src/HoaVoting.Api/Dtos/Dtos.cs tests/HoaVoting.Tests/ProposalLifecycleTests.cs`

Interfaces this task provides to later tasks: HTTP — `POST api/associations/{a}/proposals` → 201 `Open` / 202 `Draft` / 502, body `ProposalResponse` with `status`, `requirePassword`, `publishError`, `missingData`; `DELETE …/proposals/{id}` → 204/409/502; `POST …/proposals/{id}/publish` → 200/202/409/502; `PUT …/proposals/{id}/end-date {endDate}` → 200/202/400/502 (body = proposal; `endDate` unchanged on 202); `POST …/close` → 409 unless `Open`; `GET api/processes/{pid}` → 404 for `Draft`/`Failed`, adds `requirePassword`, `orgAddress`.

### Task 6: Price, integrator status, homeowner password endpoints
Model: standard   Repo: same   Files: src/HoaVoting.Api/Dtos/Dtos.cs, src/HoaVoting.Api/Controllers/AssociationsController.cs, src/HoaVoting.Api/Controllers/HomeownersController.cs, src/HoaVoting.Api/Controllers/IntegratorController.cs, tests/HoaVoting.Tests/BillingAndPasswordTests.cs
Goal: spec D and the member half of F; AC4, AC5, AC7. `GET api/associations/{id}/price?requirePassword` (0 voters answered locally, no `/pricing` call); `GET api/integrator` (admin; balance null when no managed org or the wallet/managed-org read fails; a failing `GET /integrator` still 502s); `AddHomeownerRequest` (`Dtos.cs:14-20`) + `PasswordHash`; `HomeownerResponse` (`:22`) + `HasPassword` (filled in `HomeownersController.cs:24-25`); `PUT …/homeowners/{memberId}/password` with the hash check and the existing-member check before `UpsertMemberAsync`.

Step 1: write the failing test

`tests/HoaVoting.Tests/BillingAndPasswordTests.cs` (new file, full content):

````csharp
using System.Net;
using HoaVoting.Api.Controllers;
using HoaVoting.Api.Dtos;
using HoaVoting.Api.Services.Vocdoni;

namespace HoaVoting.Tests;

// AC4 (price), AC5 (integrator wallet), AC7 (member password hash at the trust boundary).
public class BillingAndPasswordTests : ControllerTestBase
{
    private static readonly string Hash = new('a', 64);

    public BillingAndPasswordTests()
    {
        Vocdoni.Members =
        [
            new() { Id = "m1", HasPassword = true },
            new() { Id = "m2" },
            new() { Id = "m3", HasPassword = true },
        ];
    }

    private AssociationsController Associations() => AsOwner(new AssociationsController(Db, Vocdoni));
    private HomeownersController Homeowners() => AsOwner(new HomeownersController(Db, Vocdoni));
    private IntegratorController Integrator() => AsAdmin(new IntegratorController(Vocdoni));

    // --- price -----------------------------------------------------------------

    [Fact]
    public async Task Price_counts_every_homeowner_or_only_password_holders()
    {
        Vocdoni.OnGetPrice = voters => new() { TotalCents = voters * 100, Currency = "eur" };

        var all = (await Associations().Price(Assoc.Id, requirePassword: false, default)).Value!;
        var withPassword = (await Associations().Price(Assoc.Id, requirePassword: true, default)).Value!;

        Assert.Equal((3, 300), (all.Voters, all.TotalCents));
        Assert.Equal((2, 200), (withPassword.Voters, withPassword.TotalCents));
        Assert.Equal(["GetPrice 3", "GetPrice 2"], Vocdoni.Calls.Where(c => c.StartsWith("GetPrice")));
    }

    [Fact]
    public async Task Price_with_no_eligible_homeowner_answers_locally()
    {
        Vocdoni.Members = [new() { Id = "m1" }];

        var res = (await Associations().Price(Assoc.Id, requirePassword: true, default)).Value!;

        Assert.Equal(new PriceResponse(0, 0, "eur", false, false), res);
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("GetPrice")); // /pricing 400s on voters=0
    }

    // --- integrator ----------------------------------------------------------------

    [Fact]
    public async Task Integrator_reads_the_balance_of_the_managedBy_wallet()
    {
        Vocdoni.OnGetIntegrator = () => new() { CanCreateManagedOrg = false, ManagedOrgsCreated = 1 };
        Vocdoni.OnGetWallet = _ => new() { BalanceCents = 4200, Currency = "eur" };

        var res = (await Integrator().Get(default)).Value!;

        Assert.Equal(new IntegratorResponse(false, 1, 4200, "eur"), res);
        Assert.Contains("GetWallet 0xintegrator", Vocdoni.Calls);
    }

    [Fact]
    public async Task Integrator_balance_is_null_without_a_managed_org()
    {
        Vocdoni.OnGetIntegratorAddress = () => null;

        var res = (await Integrator().Get(default)).Value!;

        Assert.Null(res.BalanceCents);
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("GetWallet"));
    }

    [Fact]
    public async Task Integrator_balance_is_null_when_the_wallet_read_is_forbidden()
    {
        Vocdoni.OnGetWallet = _ => throw new VocdoniApiException(HttpStatusCode.Forbidden, """{"error":"scope","code":40157}""");

        var res = (await Integrator().Get(default)).Value!;

        Assert.True(res.CanCreateManagedOrg);
        Assert.Null(res.BalanceCents);
    }

    // --- member password -------------------------------------------------------------

    [Theory]
    [InlineData("hunter2")] // plaintext
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // uppercase
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 63 chars
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")] // trailing newline
    public async Task A_password_that_is_not_the_client_hash_is_a_400(string bad)
    {
        Assert.Equal(400, StatusOf(await Homeowners().SetPassword(Assoc.Id, "m1", new(bad), default)));
        Assert.Equal(400, StatusOf(await Homeowners().Add(Assoc.Id, new("Ann", null, null, null, "7", null, bad), default)));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("UpsertMember") || c.StartsWith("AddMembers"));
    }

    [Fact]
    public async Task A_valid_hash_reaches_the_upstream_member_password()
    {
        Assert.Equal(204, StatusOf(await Homeowners().SetPassword(Assoc.Id, "m2", new(Hash), default)));
        Assert.Equal(("m2", Hash), (Vocdoni.LastUpsert!.Id, Vocdoni.LastUpsert.Password));

        await Homeowners().Add(Assoc.Id, new("Ann", null, null, null, "7", null, Hash), default);
        Assert.Equal(Hash, Assert.Single(Vocdoni.LastAdded!).Password);
    }

    [Fact]
    public async Task Setting_a_password_for_an_unknown_member_is_a_404_without_upstream_write()
    {
        Assert.Equal(404, StatusOf(await Homeowners().SetPassword(Assoc.Id, "not-a-member", new(Hash), default)));
        Assert.DoesNotContain(Vocdoni.Calls, c => c.StartsWith("UpsertMember"));
    }

    [Fact]
    public async Task Homeowner_list_reports_who_has_a_password()
    {
        var res = await Homeowners().List(Assoc.Id, default);

        Assert.Equal([true, false, true], res.Value!.Select(h => h.HasPassword));
    }
}
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: build errors, e.g. `error CS0246: The type or namespace name 'IntegratorController' could not be found` and `'HomeownersController' does not contain a definition for 'SetPassword'`

Step 2: implement

`src/HoaVoting.Api/Controllers/IntegratorController.cs` (new file, full content):

````csharp
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
````

````diff
diff --git a/src/HoaVoting.Api/Controllers/AssociationsController.cs b/src/HoaVoting.Api/Controllers/AssociationsController.cs
index 1793bb0..42e4ef8 100644
--- a/src/HoaVoting.Api/Controllers/AssociationsController.cs
+++ b/src/HoaVoting.Api/Controllers/AssociationsController.cs
@@ -100,6 +100,27 @@ public class AssociationsController(AppDbContext db, IVocdoniClient vocdoni) : A
         return new AssociationResponse(assoc.Id, assoc.Name, assoc.Owner!.Email, assoc.VocdoniOrgAddress, assoc.CreatedAt);
     }
 
+    /// <summary>
+    /// Estimated pay-per-process price (saas-backend #819) of a proposal over the current homeowners —
+    /// only those with a password when <paramref name="requirePassword"/>, since the others are left out
+    /// of a password census. Paid from the integrator wallet at publish. Upstream needs voters ≥ 1, so
+    /// zero voters answers locally.
+    /// </summary>
+    [HttpGet("{id:int}/price")]
+    public async Task<ActionResult<PriceResponse>> Price(int id, [FromQuery] bool requirePassword, CancellationToken ct)
+    {
+        var assoc = await db.Associations.FindAsync([id], ct);
+        if (assoc is null) return NotFound();
+        if (!Authorization.AssociationAccess.CanAccess(CurrentRole, CurrentUserId, assoc)) return Forbid();
+
+        var members = await vocdoni.ListMembersAsync(assoc.VocdoniOrgAddress, ct);
+        var voters = members.Count(m => m.Id is not null && (!requirePassword || m.HasPassword));
+        if (voters == 0) return new PriceResponse(0, 0, "eur", false, false);
+
+        var price = await vocdoni.GetPriceAsync(voters, ct);
+        return new PriceResponse(voters, price.TotalCents, price.Currency ?? "eur", price.QuoteRecommended, price.QuoteRequired);
+    }
+
     /// <summary>
     /// Removes an association: deletes the Vocdoni managed org (cascade — members, censuses,
     /// processes, bundles) and reclaims integrator quota, then drops its proposals and owner login
diff --git a/src/HoaVoting.Api/Controllers/HomeownersController.cs b/src/HoaVoting.Api/Controllers/HomeownersController.cs
index d1aecd0..ba15ca3 100644
--- a/src/HoaVoting.Api/Controllers/HomeownersController.cs
+++ b/src/HoaVoting.Api/Controllers/HomeownersController.cs
@@ -22,7 +22,7 @@ public class HomeownersController(AppDbContext db, IVocdoniClient vocdoni) : Api
 
         var members = await vocdoni.ListMembersAsync(assoc!.VocdoniOrgAddress, ct);
         return members.Select(m => new HomeownerResponse(
-            m.Id ?? "", m.Name ?? "", m.Surname, m.Email ?? "", m.MemberNumber, m.Weight)).ToList();
+            m.Id ?? "", m.Name ?? "", m.Surname, m.Email ?? "", m.MemberNumber, m.Weight, m.HasPassword)).ToList();
     }
 
     /// <summary>Add a homeowner to the association.</summary>
@@ -31,6 +31,7 @@ public class HomeownersController(AppDbContext db, IVocdoniClient vocdoni) : Api
     {
         var (assoc, error) = await ResolveAsync(associationId, ct);
         if (error is not null) return error;
+        if (req.PasswordHash is not null && !IsPasswordHash(req.PasswordHash)) return BadRequest(PasswordHashRule);
 
         var member = new VocdoniOrgMember
         {
@@ -40,6 +41,7 @@ public class HomeownersController(AppDbContext db, IVocdoniClient vocdoni) : Api
             Phone = req.Phone,
             MemberNumber = req.MemberNumber,
             Weight = req.Weight,
+            Password = req.PasswordHash,
         };
         var result = await vocdoni.AddMembersAsync(assoc!.VocdoniOrgAddress, [member], ct);
         return Ok(result);
@@ -56,6 +58,30 @@ public class HomeownersController(AppDbContext db, IVocdoniClient vocdoni) : Api
         return NoContent();
     }
 
+    /// <summary>
+    /// Set a homeowner's password (saas-backend #760) from its browser-computed hash. The member must
+    /// exist: upstream's upsert treats an unknown id as a create, which would add a phantom member to
+    /// live censuses (and can charge the integrator for the growth).
+    /// </summary>
+    [HttpPut("{memberId}/password")]
+    public async Task<ActionResult> SetPassword(int associationId, string memberId, SetPasswordRequest req, CancellationToken ct)
+    {
+        var (assoc, error) = await ResolveAsync(associationId, ct);
+        if (error is not null) return error;
+        if (!IsPasswordHash(req.PasswordHash)) return BadRequest(PasswordHashRule);
+
+        var members = await vocdoni.ListMembersAsync(assoc!.VocdoniOrgAddress, ct);
+        if (!members.Any(m => m.Id == memberId)) return NotFound();
+
+        await vocdoni.UpsertMemberAsync(assoc.VocdoniOrgAddress, new VocdoniOrgMember { Id = memberId, Password = req.PasswordHash }, ct);
+        return NoContent();
+    }
+
+    private const string PasswordHashRule = "passwordHash must be the 64-character lowercase hex client hash (hashMemberPassword), never the password.";
+
+    // Trust boundary: only the client hash may pass. Char check, not a regex: "$" would accept a trailing newline.
+    internal static bool IsPasswordHash(string? s) => s is { Length: 64 } && s.All(char.IsAsciiHexDigitLower);
+
     private async Task<(Association?, ActionResult?)> ResolveAsync(int associationId, CancellationToken ct)
     {
         var assoc = await db.Associations.FindAsync([associationId], ct);
diff --git a/src/HoaVoting.Api/Dtos/Dtos.cs b/src/HoaVoting.Api/Dtos/Dtos.cs
index d6cc0ba..a2af9cf 100644
--- a/src/HoaVoting.Api/Dtos/Dtos.cs
+++ b/src/HoaVoting.Api/Dtos/Dtos.cs
@@ -17,9 +17,21 @@ public record AddHomeownerRequest(
     string? Email,          // optional member contact detail
     string? Phone,
     string MemberNumber,
-    string? Weight);
+    string? Weight,
+    // Client hash of the member's password (saas-backend #760): 64 lowercase hex, computed in the
+    // browser with hashMemberPassword(orgAddress, password). The plaintext never reaches this backend.
+    string? PasswordHash = null);
 
-public record HomeownerResponse(string Id, string Name, string? Surname, string Email, string? MemberNumber, string? Weight);
+public record HomeownerResponse(string Id, string Name, string? Surname, string Email, string? MemberNumber, string? Weight, bool HasPassword = false);
+
+public record SetPasswordRequest(string PasswordHash);
+
+// Estimated pay-per-process price of a proposal over the current homeowners (saas-backend #819).
+public record PriceResponse(int Voters, int TotalCents, string Currency, bool QuoteRecommended, bool QuoteRequired);
+
+// Integrator status for the admin page. BalanceCents/Currency are null when unknown (no managed org yet,
+// or the API key cannot read the wallet).
+public record IntegratorResponse(bool CanCreateManagedOrg, int ManagedOrgsCreated, long? BalanceCents, string? Currency);
 
 // A proposal is a multi-question voting process (saas-backend #571). Voters always auth by member
 // number (no 2FA); each question is its own on-chain election.
````

Run: `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`
Expected: `Passed!  - Failed:     0, Passed:    85, Skipped:     0, Total:    85`

Step 3: commit
`git add src/HoaVoting.Api/Controllers/IntegratorController.cs tests/HoaVoting.Tests/BillingAndPasswordTests.cs && git -c commit.gpgsign=false commit -m "feat(api): add price, integrator status and homeowner password endpoints" -- src/HoaVoting.Api/Dtos/Dtos.cs src/HoaVoting.Api/Controllers/AssociationsController.cs src/HoaVoting.Api/Controllers/HomeownersController.cs src/HoaVoting.Api/Controllers/IntegratorController.cs tests/HoaVoting.Tests/BillingAndPasswordTests.cs`

Interfaces this task provides to later tasks: `GET api/associations/{id}/price?requirePassword=bool` → `{voters, totalCents, currency, quoteRecommended, quoteRequired}`; `GET api/integrator` → `{canCreateManagedOrg, managedOrgsCreated, balanceCents|null, currency|null}`; homeowners list items carry `hasPassword`; `POST …/homeowners` accepts `passwordHash`; `PUT …/homeowners/{memberId}/password {passwordHash}` → 204/400/404.

### Task 7: SDK bump, `elections` rename, password hashing in voting.js
Model: fast   Repo: same   Files: web/package.json, web/package-lock.json, web/src/voting.js, web/src/voting.test.js
Goal: spec G (SDK) and the voter half of F; AC8. `@vocdoni/api-client` ^2.0.0 → ^2.8.0 and `@vocdoni/api-voting` ^1.1.0 → ^1.3.3 (`package.json:13-14`); `client.processes.authStep0` / `signBatch` (`voting.js:33, 45`) → `client.elections.*`; `castProcessVotes` takes optional `password` + `orgAddress` and sends `hashMemberPassword(orgAddress, password)` (api-client ≥ 2.7.0: `hashMemberPassword(orgAddress: string, password: string): Promise<string>`, throws on a blank password or a non-`0x`+40-hex address) as `password` to step 0; the "100 questions" comment (`voting.js:73`) says 15. The test mock (`voting.test.js:26-33`) exposes only `elections`, so any leftover `processes` call fails.

Step 1: bump the SDK and write the failing test
```bash
cd web && npm install @vocdoni/api-client@^2.8.0 @vocdoni/api-voting@^1.3.3
```
Expected: `package.json` shows `"@vocdoni/api-client": "^2.8.0"`, `"@vocdoni/api-voting": "^1.3.3"`; the lockfile resolves 2.8.0 / 1.3.3.

````diff
diff --git a/web/src/voting.test.js b/web/src/voting.test.js
index 97e6b05..8cbaf91 100644
--- a/web/src/voting.test.js
+++ b/web/src/voting.test.js
@@ -15,6 +15,7 @@ const h = vi.hoisted(() => {
     JobFailedError,
     signerCount: 0,
     authStep0: vi.fn(),
+    hashMemberPassword: vi.fn(),
     signBatch: vi.fn(),
     signBlindCspBallots: vi.fn(),
     voteBatch: vi.fn(),
@@ -23,11 +24,12 @@ const h = vi.hoisted(() => {
   }
 })
 
+// Only `elections`: `processes` is a deprecated alias since api-client 2.1.0, so using it fails here.
 vi.mock('@vocdoni/api-client', () => ({
   JobFailedError: h.JobFailedError,
+  hashMemberPassword: h.hashMemberPassword,
   VocdoniApiClient: class {
-    processes = { authStep0: h.authStep0, signBatch: h.signBatch }
-    elections = { voteBatch: h.voteBatch }
+    elections = { authStep0: h.authStep0, signBatch: h.signBatch, voteBatch: h.voteBatch }
     jobs = { waitFor: h.waitFor }
   },
 }))
@@ -58,12 +60,13 @@ const SIGNED = [
   { upstreamId: 'q3', signature: 'sig3', weight: '0x3' },
 ]
 
-const cast = (answers = ANSWERS, { anonymous = false } = {}) =>
+const cast = (answers = ANSWERS, { anonymous = false, password } = {}) =>
   castProcessVotes({
     apiUrl: 'https://api.test',
     processId: 'p1',
     chainId: 'vocdoni-test-1',
     memberNumber: '42',
+    ...(password === undefined ? {} : { password, orgAddress: '0xorg' }),
     anonymous,
     answers,
   })
@@ -74,6 +77,7 @@ beforeEach(() => {
   vi.clearAllMocks()
   h.signerCount = 0
   h.authStep0.mockResolvedValue({ authToken: 'tok' })
+  h.hashMemberPassword.mockResolvedValue('ab'.repeat(32))
   h.signBatch.mockResolvedValue({ signatures: SIGNED })
   h.signBlindCspBallots.mockResolvedValue(SIGNED)
   h.buildVoteTransaction.mockImplementation((o) => `tx-${o.processId}`)
@@ -107,6 +111,27 @@ describe('castProcessVotes', () => {
     expect(h.signBlindCspBallots).not.toHaveBeenCalled()
   })
 
+  it('sends no password without one', async () => {
+    await cast()
+
+    expect(h.authStep0.mock.calls[0][1]).not.toHaveProperty('password')
+    expect(h.hashMemberPassword).not.toHaveBeenCalled()
+  })
+
+  it('hashes the password with the org address and sends only the hash to step 0', async () => {
+    await cast(ANSWERS, { password: 'correct horse' })
+
+    expect(h.hashMemberPassword).toHaveBeenCalledWith('0xorg', 'correct horse')
+    expect(h.authStep0).toHaveBeenCalledWith('p1', { memberNumber: '42', password: 'ab'.repeat(32) })
+  })
+
+  it('does not authenticate when hashing fails (blank password, insecure context)', async () => {
+    h.hashMemberPassword.mockRejectedValue(new Error('empty password'))
+
+    await expect(cast(ANSWERS, { password: ' ' })).rejects.toThrow('empty password')
+    expect(h.authStep0).not.toHaveBeenCalled()
+  })
+
   it('relays the whole ballot in exactly one batch, in answers order', async () => {
     await cast()
 
````

Run: `cd web && npm test`
Expected: `Tests  18 failed (18)` with `TypeError: Cannot read properties of undefined (reading 'authStep0')`

Step 2: implement

````diff
diff --git a/web/src/voting.js b/web/src/voting.js
index 5e06330..34759d1 100644
--- a/web/src/voting.js
+++ b/web/src/voting.js
@@ -1,4 +1,4 @@
-import { VocdoniApiClient, JobFailedError } from '@vocdoni/api-client'
+import { VocdoniApiClient, JobFailedError, hashMemberPassword } from '@vocdoni/api-client'
 import { buildVoteTransaction, EphemeralSigner, signBlindCspBallots, ProofCA_Type } from '@vocdoni/api-voting'
 
 // Casts ballots on a multi-question voting process (saas-backend #571) entirely through the SaaS API.
@@ -21,16 +21,21 @@ import { buildVoteTransaction, EphemeralSigner, signBlindCspBallots, ProofCA_Typ
 // ranks; cumulative → [v0..vN-1] credit per choice); `memo` is the optional free-text note attached
 // when the open "Other" choice is picked (#577).
 //
+// `password` (only on a password census, saas-backend #760) is the voter's plaintext: it is hashed here
+// with `orgAddress` and only the hash leaves the browser. Omit it on a member-number-only census.
+//
 // Returns one outcome per answered question, in `answers` order:
 //   [{ upstreamId, ok, voteID, error }]
 // A batch rejected synchronously (bad envelope, unknown process, repeated nullifier, queue full)
 // throws instead — nothing was relayed, so the caller can fix and retry safely.
-export async function castProcessVotes({ apiUrl, processId, chainId, memberNumber, anonymous, answers }) {
+export async function castProcessVotes({ apiUrl, processId, chainId, memberNumber, password, orgAddress, anonymous, answers }) {
   const client = new VocdoniApiClient({ apiUrl })
 
   // 1. Auth once per process (identical for anonymous). Auth-only census ⇒ the step-0 token is
-  //    already verified (no OTP).
-  const { authToken } = await client.processes.authStep0(processId, { memberNumber })
+  //    already verified (no OTP). hashMemberPassword throws on a blank password or outside a secure context.
+  const credentials = { memberNumber }
+  if (password != null) credentials.password = await hashMemberPassword(orgAddress, password)
+  const { authToken } = await client.elections.authStep0(processId, credentials)
   if (!authToken) throw new Error('Authentication failed — check your member number.')
 
   // 2. One fresh ephemeral identity per question, created BEFORE signing: the address is inside the
@@ -42,7 +47,7 @@ export async function castProcessVotes({ apiUrl, processId, chainId, memberNumbe
   //    election id — both endpoints are process-scoped.
   const signatures = anonymous
     ? await signBlindCspBallots({ processId, authToken, client, ballots })
-    : (await client.processes.signBatch(processId, { authToken, ballots })).signatures
+    : (await client.elections.signBatch(processId, { authToken, ballots })).signatures
   const byUpstream = new Map((signatures ?? []).map((s) => [s.upstreamId, s]))
 
   // 4. Build envelopes for the signed ballots; sign failures turn into outcome rows below.
@@ -70,7 +75,7 @@ export async function castProcessVotes({ apiUrl, processId, chainId, memberNumbe
     })
   })
 
-  // 5. Relay in one call — but only when something was signed. A process caps at 100 questions and a
+  // 5. Relay in one call — but only when something was signed. A process caps at 15 questions and a
   //    batch at 100 votes, so a full ballot always fits — no chunking needed.
   let votes = []
   if (envelopes.length > 0) {
````

Run: `cd web && npm test && npm run build`
Expected: `Tests  18 passed (18)` and vite `✓ built`

Step 3: commit
`git -c commit.gpgsign=false commit -m "feat(web): move to integrator sdk 2.8 and send the hashed member password" -- web/package.json web/package-lock.json web/src/voting.js web/src/voting.test.js`

Interfaces this task provides to later tasks: `castProcessVotes({ apiUrl, processId, chainId, memberNumber, password?, orgAddress?, anonymous, answers })`; SDK errors are `VocdoniApiError` with `.code` (e.g. 40802, 40017); `hashMemberPassword` importable from `@vocdoni/api-client`.

### Task 8: SPA — publish states, price, end date, passwords, integrator panel, voter errors
Model: standard   Repo: same   Files: web/src/components/Proposals.jsx, web/src/components/Memberbase.jsx, web/src/components/AdminPage.jsx, web/src/components/VotingPage.jsx, web/src/styles.css
Goal: the UI half of spec C ("Owner UI states"), D, E and F. Proposals: list badge/actions by status (base derives the badge from `isFinished` for every row at `Proposals.jsx:277-279`, shows Close whenever unfinished at `:288`, the eligibility editor at `:306` and the voting link at `:320` for every row): `Draft` → "Publishing…" + Delete (confirm), 5 s refetch while any row is `Draft`; `Failed` → badge, `publishError`, Retry publish + Delete; `isFinished` only for `Open`/`Closed`; voting link only for `Open`/`Closed`; end-date editor and eligibility only for open rows; create reads the returned `status` and `missingData`; password toggle; price line refetched on the toggle. Memberbase: optional password on add and per-row "Set password" (hashed with `assoc.vocdoniOrgAddress` before sending), "Password" column from `hasPassword`. AdminPage: integrator panel loaded separately from the list (failure → "Integrator status unavailable"; `canCreateManagedOrg` false → top-up notice). VotingPage: password input when `requirePassword`, passes `password` + `orgAddress`, maps 40802/40017. styles: `.s-failed`, `form.inline`.

Step 1: no automated UI test exists (vitest covers `voting.js` only); the check for this task is the build plus the manual pass in Step 2.

Step 2: implement

````diff
diff --git a/web/src/components/AdminPage.jsx b/web/src/components/AdminPage.jsx
index 25ef89e..2b2cb3e 100644
--- a/web/src/components/AdminPage.jsx
+++ b/web/src/components/AdminPage.jsx
@@ -3,12 +3,32 @@ import { api } from '../api.js'
 
 const short = (s) => (s ? `${s.slice(0, 8)}…${s.slice(-4)}` : '')
 
+// Integrator wallet + managed-org cap (saas-backend #819). info: null = loading, false = unavailable.
+function IntegratorStatus({ info }) {
+  if (info === null) return null
+  if (info === false) return <p className="helper">Integrator status unavailable.</p>
+  const n = info.managedOrgsCreated
+  return (
+    <>
+      <p className="helper">
+        Integrator wallet: <b>{info.balanceCents == null ? 'unavailable' : `€${(info.balanceCents / 100).toFixed(2)}`}</b>
+        {' · '}
+        {n} association{n === 1 ? '' : 's'} created
+      </p>
+      {!info.canCreateManagedOrg && (
+        <div className="error">Top up the integrator wallet in the Vocdoni portal before creating another association.</div>
+      )}
+    </>
+  )
+}
+
 export default function AdminPage() {
   const [assocs, setAssocs] = useState([])
   const [loading, setLoading] = useState(true)
   const [form, setForm] = useState({ name: '', ownerEmail: '', ownerPassword: '' })
   const [error, setError] = useState('')
   const [busy, setBusy] = useState(false)
+  const [integrator, setIntegrator] = useState(null)
 
   async function load() {
     setLoading(true)
@@ -20,8 +40,11 @@ export default function AdminPage() {
       setLoading(false)
     }
   }
+  // Separate from load(): a failing integrator read must not block the associations list.
+  const loadIntegrator = () => api('/integrator').then(setIntegrator).catch(() => setIntegrator(false))
   useEffect(() => {
     load()
+    loadIntegrator()
   }, [])
 
   async function create(e) {
@@ -32,6 +55,7 @@ export default function AdminPage() {
       await api('/associations', { method: 'POST', body: form })
       setForm({ name: '', ownerEmail: '', ownerPassword: '' })
       await load()
+      loadIntegrator()
     } catch (e) {
       setError(e.message)
     } finally {
@@ -72,6 +96,7 @@ export default function AdminPage() {
           <button disabled={busy}>{busy ? 'Creating…' : 'Create association'}</button>
         </form>
         <p className="helper">Creates a Vocdoni managed organization and an owner login that can manage it.</p>
+        <IntegratorStatus info={integrator} />
       </section>
 
       <section className="card">
diff --git a/web/src/components/Memberbase.jsx b/web/src/components/Memberbase.jsx
index fbff70b..76e57e6 100644
--- a/web/src/components/Memberbase.jsx
+++ b/web/src/components/Memberbase.jsx
@@ -1,10 +1,18 @@
 import { useEffect, useRef, useState } from 'react'
+import { hashMemberPassword } from '@vocdoni/api-client'
 import { api } from '../api.js'
 
+// Member passwords (saas-backend #760) are hashed here, in the browser, with the org address; only the
+// 64-hex hash goes to the backend. hashMemberPassword throws on a blank password or outside a secure
+// context (HTTPS or localhost) — its message is shown as is.
+const hashFor = (assoc, password) => hashMemberPassword(assoc.vocdoniOrgAddress, password)
+
 export default function Memberbase({ assoc }) {
   const [members, setMembers] = useState([])
   const [loading, setLoading] = useState(true)
-  const [form, setForm] = useState({ name: '', memberNumber: '', email: '' })
+  const [form, setForm] = useState({ name: '', memberNumber: '', email: '', password: '' })
+  const [pwFor, setPwFor] = useState(null) // member id whose password is being set
+  const [pw, setPw] = useState('')
   const [error, setError] = useState('')
   const [note, setNote] = useState('')
   const [busy, setBusy] = useState(false)
@@ -34,8 +42,9 @@ export default function Memberbase({ assoc }) {
     try {
       const body = { name: form.name, memberNumber: form.memberNumber }
       if (form.email) body.email = form.email
+      if (form.password) body.passwordHash = await hashFor(assoc, form.password)
       await api(base, { method: 'POST', body })
-      setForm({ name: '', memberNumber: '', email: '' })
+      setForm({ name: '', memberNumber: '', email: '', password: '' })
       await load()
     } catch (e) {
       setError(e.message)
@@ -44,6 +53,19 @@ export default function Memberbase({ assoc }) {
     }
   }
 
+  async function savePassword(id) {
+    setError('')
+    try {
+      const passwordHash = await hashFor(assoc, pw)
+      await api(`${base}/${id}/password`, { method: 'PUT', body: { passwordHash } })
+      setPwFor(null)
+      setPw('')
+      await load()
+    } catch (e) {
+      setError(e.message)
+    }
+  }
+
   async function remove(id) {
     if (!confirm('Remove this homeowner?')) return
     setError('')
@@ -101,6 +123,10 @@ export default function Memberbase({ assoc }) {
             Email (optional)
             <input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
           </label>
+          <label>
+            Password (optional, for proposals that require one)
+            <input type="password" autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
+          </label>
           <button disabled={busy}>Add</button>
         </form>
 
@@ -128,14 +154,30 @@ export default function Memberbase({ assoc }) {
         ) : (
           <table>
             <thead>
-              <tr><th>Name</th><th>Member #</th><th></th></tr>
+              <tr><th>Name</th><th>Member #</th><th>Password</th><th></th></tr>
             </thead>
             <tbody>
               {members.map((m) => (
                 <tr key={m.id}>
                   <td>{m.name}</td>
                   <td>{m.memberNumber}</td>
+                  <td>{m.hasPassword ? '✓ set' : '—'}</td>
                   <td>
+                    {pwFor === m.id ? (
+                      <form
+                        className="inline"
+                        onSubmit={(e) => {
+                          e.preventDefault()
+                          savePassword(m.id)
+                        }}
+                      >
+                        <input type="password" autoComplete="new-password" value={pw} onChange={(e) => setPw(e.target.value)} placeholder="New password" required autoFocus />
+                        <button className="link">Save</button>
+                        <button type="button" className="link" onClick={() => setPwFor(null)}>Cancel</button>
+                      </form>
+                    ) : (
+                      <button className="link" onClick={() => { setPwFor(m.id); setPw('') }}>Set password</button>
+                    )}{' '}
                     <button className="link danger" onClick={() => remove(m.id)}>Remove</button>
                   </td>
                 </tr>
diff --git a/web/src/components/Proposals.jsx b/web/src/components/Proposals.jsx
index bf3a931..20f663f 100644
--- a/web/src/components/Proposals.jsx
+++ b/web/src/components/Proposals.jsx
@@ -10,13 +10,23 @@ const toLocalInput = (d) =>
 const short = (s) => (s ? `${s.slice(0, 10)}…${s.slice(-6)}` : '')
 const ONE_DAY = 864e5
 const plusDay = (localStr) => toLocalInput(new Date(new Date(localStr).getTime() + ONE_DAY))
+const euros = (cents) => `€${(cents / 100).toFixed(2)}`
+
+// Pay-per-process estimate (saas-backend #819). Order matters: totalCents 0 with quoteRequired is
+// "no price", not free.
+function priceText(p) {
+  if (p.voters === 0) return 'No eligible homeowners.'
+  if (p.quoteRequired) return 'Over 50 000 voters: this proposal needs a custom quote from Vocdoni.'
+  if (p.totalCents === 0) return `Free (${p.voters} voters).`
+  return `Estimated cost: ${euros(p.totalCents)} for ${p.voters} voters, paid from the integrator wallet.`
+}
 
 // openIndex = the choice marked as the free-text "Other" option (#577), or -1. Single-choice only.
 // budget/costExponent apply to cumulative questions only (costExponent 2 = quadratic).
 const blankQuestion = () => ({ title: '', choices: ['Yes', 'No'], kind: 'single', openIndex: -1, budget: 10, costExponent: 1 })
 const blankForm = () => {
   const start = toLocalInput(new Date())
-  return { title: '', description: '', startDate: start, endDate: plusDay(start), questions: [blankQuestion()], anonymous: false }
+  return { title: '', description: '', startDate: start, endDate: plusDay(start), questions: [blankQuestion()], anonymous: false, requirePassword: false }
 }
 
 const KINDS = [
@@ -31,8 +41,10 @@ export default function Proposals({ assoc }) {
   const [loading, setLoading] = useState(true)
   const [form, setForm] = useState(blankForm())
   const [error, setError] = useState('')
+  const [note, setNote] = useState('')
   const [busy, setBusy] = useState(false)
   const [closing, setClosing] = useState(() => new Set()) // proposal ids with a pending close
+  const [price, setPrice] = useState(null)
 
   const base = `/associations/${assoc.id}/proposals`
 
@@ -49,6 +61,21 @@ export default function Proposals({ assoc }) {
   useEffect(() => {
     load()
   }, [assoc.id])
+  const refresh = () => api(base).then(setItems).catch(() => {}) // no "Loading…" flicker
+
+  // Re-priced when the password toggle changes: a password census leaves out homeowners without one.
+  useEffect(() => {
+    setPrice(null)
+    api(`/associations/${assoc.id}/price?requirePassword=${form.requirePassword}`).then(setPrice).catch(() => setPrice(null))
+  }, [assoc.id, form.requirePassword])
+
+  // A publish can outlive the create request (202 + Draft): refetch until no proposal is publishing.
+  const publishing = items.some((p) => p.status === 'Draft')
+  useEffect(() => {
+    if (!publishing) return
+    const id = setInterval(refresh, 5000)
+    return () => clearInterval(id)
+  }, [publishing, base])
 
   // Question + choice editing (functional updates — no stale closures).
   const updateQ = (qi, fn) => setForm((f) => ({ ...f, questions: f.questions.map((q, i) => (i === qi ? fn(q) : q)) }))
@@ -71,6 +98,7 @@ export default function Proposals({ assoc }) {
   async function create(e) {
     e.preventDefault()
     setError('')
+    setNote('')
     setBusy(true)
     try {
       const questions = form.questions.map((q) => ({
@@ -85,7 +113,8 @@ export default function Proposals({ assoc }) {
       if (questions.some((q) => !q.title)) throw new Error('Every question needs a title.')
       if (questions.some((q) => q.choices.length < 2)) throw new Error('Every question needs at least two choices.')
       if (questions.some((q) => q.kind === 'cumulative' && !(q.budget > 0))) throw new Error('Cumulative questions need a budget of at least 1.')
-      await api(base, {
+      // 201 (Open) and 202 (Draft, still publishing) look alike through api(): read the status.
+      const created = await api(base, {
         method: 'POST',
         body: {
           title: form.title,
@@ -94,8 +123,16 @@ export default function Proposals({ assoc }) {
           endDate: new Date(form.endDate).toISOString(),
           questions,
           anonymous: form.anonymous,
+          requirePassword: form.requirePassword,
         },
       })
+      const left = created?.missingData?.length ?? 0
+      setNote(
+        [
+          created?.status === 'Draft' ? 'Publishing continues on chain; the list updates once it is live.' : '',
+          left ? `${left} homeowners were left out of this proposal (missing data, e.g. no password). Setting it later does not add them.` : '',
+        ].filter(Boolean).join(' '),
+      )
       setForm(blankForm())
       await load()
     } catch (e) {
@@ -105,6 +142,32 @@ export default function Proposals({ assoc }) {
     }
   }
 
+  // Draft: only when publishing is stuck. Failed: upstream may refuse (409) if elections were submitted —
+  // Retry publish resolves those.
+  async function remove(p) {
+    const msg = p.status === 'Draft'
+      ? 'Delete this proposal? Only do this if publishing is stuck.'
+      : 'Delete this proposal and its upstream draft?'
+    if (!confirm(msg)) return
+    setError('')
+    try {
+      await api(`${base}/${p.id}`, { method: 'DELETE' })
+    } catch (e) {
+      setError(e.message)
+    }
+    await refresh()
+  }
+
+  async function retry(p) {
+    setError('')
+    try {
+      await api(`${base}/${p.id}/publish`, { method: 'POST' })
+    } catch (e) {
+      setError(e.message)
+    }
+    await refresh()
+  }
+
   const unmarkClosing = (pid) =>
     setClosing((s) => {
       const n = new Set(s)
@@ -241,9 +304,15 @@ export default function Proposals({ assoc }) {
             <input type="checkbox" checked={form.anonymous} onChange={(e) => setForm({ ...form, anonymous: e.target.checked })} />
             <span>Anonymous voting</span>
           </label>
+          <label className="check" title="Voters also enter the password set for them in the memberbase.">
+            <input type="checkbox" checked={form.requirePassword} onChange={(e) => setForm({ ...form, requirePassword: e.target.checked })} />
+            <span>Require a password (homeowners without one are left out)</span>
+          </label>
+          {price && <p className="helper">{priceText(price)}</p>}
 
           {error && <div className="error">{error}</div>}
-          <button disabled={busy}>{busy ? 'Publishing… (~10–30s)' : 'Create & publish'}</button>
+          {note && <div className="note">{note}</div>}
+          <button disabled={busy}>{busy ? 'Publishing…' : 'Create & publish'}</button>
         </form>
       </section>
 
@@ -259,8 +328,12 @@ export default function Proposals({ assoc }) {
         ) : (
           <ul className="proposals">
             {items.map((p) => {
-              const finished = isFinished(p)
-              const status = finished ? 'Closed' : p.status
+              // Draft (publishing) and Failed never reached the chain: isFinished's end-date rule
+              // only applies to live proposals.
+              const live = p.status === 'Open' || p.status === 'Closed'
+              const finished = live && isFinished(p)
+              const open = p.status === 'Open' && !finished
+              const status = p.status === 'Draft' ? 'Publishing…' : finished ? 'Closed' : p.status
               return (
                 <li key={p.id}>
                   <div className="p-head">
@@ -268,17 +341,27 @@ export default function Proposals({ assoc }) {
                     <div className="p-actions">
                       {closing.has(p.id) ? (
                         <span className="status s-closing">Closing… ⟳</span>
-                      ) : (
-                        !finished && <button className="link danger" onClick={() => close(p.id)}>Close</button>
-                      )}
+                      ) : open ? (
+                        <button className="link danger" onClick={() => close(p.id)}>Close</button>
+                      ) : p.status === 'Failed' ? (
+                        <>
+                          <button className="link" onClick={() => retry(p)}>Retry publish</button>
+                          <button className="link danger" onClick={() => remove(p)}>Delete</button>
+                        </>
+                      ) : p.status === 'Draft' ? (
+                        <button className="link danger" onClick={() => remove(p)}>Delete</button>
+                      ) : null}
                     </div>
                   </div>
                   {p.description && <p className="p-desc small">{p.description}</p>}
                   <div className="p-meta">
-                    <span className={`status s-${status.toLowerCase()}`}>{status}</span>
+                    <span className={`status s-${finished ? 'closed' : p.status.toLowerCase()}`}>{status}</span>
                     {p.anonymous && <span className="badge-anon">anonymous</span>}
+                    {p.requirePassword && <span className="badge-anon">password</span>}
                     <span className="mono small muted" title={p.vocdoniProcessId}>{short(p.vocdoniProcessId)}</span>
                   </div>
+                  {p.status === 'Failed' && p.publishError && <div className="error small">{p.publishError}</div>}
+                  {open && <EndDateEditor url={`${base}/${p.id}/end-date`} endDate={p.endDate} onChanged={refresh} />}
                   <div className="p-questions">
                     {p.questions.map((q) => (
                       <div key={q.id} className="p-question-block">
@@ -287,8 +370,8 @@ export default function Proposals({ assoc }) {
                           {q.status && <span className={`status s-${q.status.toLowerCase()}`}>{q.status}</span>}
                         </div>
                         <QuestionResults q={q} />
-                        {!finished && q.upstreamId && (
-                          <EligibilityEditor assoc={assoc} proposal={p} q={q} onChanged={() => api(base).then(setItems).catch(() => {})} />
+                        {open && q.upstreamId && (
+                          <EligibilityEditor assoc={assoc} proposal={p} q={q} onChanged={refresh} />
                         )}
                         {q.memos?.length > 0 && (
                           <div className="memos">
@@ -301,7 +384,7 @@ export default function Proposals({ assoc }) {
                       </div>
                     ))}
                   </div>
-                  {p.vocdoniProcessId && <VotingLink processId={p.vocdoniProcessId} />}
+                  {live && p.vocdoniProcessId && <VotingLink processId={p.vocdoniProcessId} />}
                 </li>
               )
             })}
@@ -416,6 +499,44 @@ function EligibilityEditor({ assoc, proposal, q, onChanged }) {
   )
 }
 
+// Extend an open proposal's end date (saas-backend #844). The answer carries the stored end: the new one
+// when the chain job finished, the old one while it is still running (the list picks it up later).
+function EndDateEditor({ url, endDate, onChanged }) {
+  const [value, setValue] = useState(() => toLocalInput(new Date(endDate)))
+  const [saving, setSaving] = useState(false)
+  const [info, setInfo] = useState('')
+  const [err, setErr] = useState('')
+
+  async function extend() {
+    setInfo('')
+    setErr('')
+    setSaving(true)
+    try {
+      const res = await api(url, { method: 'PUT', body: { endDate: new Date(value).toISOString() } })
+      if (new Date(res.endDate) < new Date(value)) setInfo('Extension submitted; it shows here once confirmed on chain.')
+      onChanged()
+    } catch (e) {
+      setErr(e.message)
+    } finally {
+      setSaving(false)
+    }
+  }
+
+  return (
+    <div className="eligibility">
+      <span className="small muted">
+        Ends{' '}
+        <input type="datetime-local" value={value} onChange={(e) => setValue(e.target.value)} />{' '}
+        <button type="button" className="link" disabled={saving} onClick={extend}>
+          {saving ? 'Extending…' : 'Extend'}
+        </button>
+      </span>
+      {info && <div className="small muted">{info}</div>}
+      {err && <div className="error small">{err}</div>}
+    </div>
+  )
+}
+
 function VotingLink({ processId }) {
   const [copied, setCopied] = useState(false)
   const url = `${location.origin}/processes/${processId}`
diff --git a/web/src/components/VotingPage.jsx b/web/src/components/VotingPage.jsx
index 38aa422..eca764a 100644
--- a/web/src/components/VotingPage.jsx
+++ b/web/src/components/VotingPage.jsx
@@ -48,6 +48,7 @@ export default function VotingPage({ processId }) {
   const [error, setError] = useState('')
   const [answers, setAnswers] = useState({}) // questionId → { selected: number[], order: number[] }
   const [memberNumber, setMemberNumber] = useState('')
+  const [password, setPassword] = useState('')
   const [casting, setCasting] = useState(false)
   const [voteErr, setVoteErr] = useState('')
   const [cast, setCast] = useState(null) // [{ upstreamId, voteID }] once cast
@@ -123,13 +124,22 @@ export default function VotingPage({ processId }) {
         processId: info.processId,
         chainId: info.chainId,
         memberNumber: memberNumber.trim(),
+        // Password census (#760): voting.js hashes it with the org address; the plaintext stays here.
+        ...(info.requirePassword ? { password, orgAddress: info.orgAddress } : {}),
         anonymous: info.anonymous,
         answers: payload,
       })
       setCast(results)
       await reload()
     } catch (e) {
-      setVoteErr(e.message || 'Could not cast the vote.')
+      // A wrong password answers like an unknown member (40802); step 0 is budgeted per hour (40017).
+      setVoteErr(
+        e.code === 40802
+          ? info.requirePassword ? 'Check your member number and password.' : 'Authentication failed — check your member number.'
+          : e.code === 40017
+            ? 'Too many attempts, try again later.'
+            : e.message || 'Could not cast the vote.',
+      )
     } finally {
       setCasting(false)
     }
@@ -236,9 +246,22 @@ export default function VotingPage({ processId }) {
               required
             />
           </label>
+          {info.requirePassword && (
+            <label>
+              Password
+              <input
+                type="password"
+                autoComplete="current-password"
+                value={password}
+                onChange={(e) => setPassword(e.target.value)}
+                placeholder="Your voting password"
+                required
+              />
+            </label>
+          )}
 
           {voteErr && <div className="error">{voteErr}</div>}
-          <button disabled={casting || !memberNumber.trim() || !allAnswered}>
+          <button disabled={casting || !memberNumber.trim() || (info.requirePassword && !password.trim()) || !allAnswered}>
             {casting ? 'Casting vote…' : `Cast vote${info.questions.length === 1 ? '' : 's'}`}
           </button>
         </form>
diff --git a/web/src/styles.css b/web/src/styles.css
index 11403ea..7b4818b 100644
--- a/web/src/styles.css
+++ b/web/src/styles.css
@@ -282,6 +282,13 @@ form {
   gap: 14px;
 }
 
+/* One-line form inside a table cell (Memberbase "Set password"). */
+form.inline {
+  flex-direction: row;
+  align-items: center;
+  gap: 8px;
+}
+
 label {
   display: flex;
   flex-direction: column;
@@ -782,6 +789,12 @@ code {
   border-color: rgba(154, 106, 26, 0.22);
 }
 
+.s-failed {
+  background: var(--danger-bg);
+  color: var(--danger);
+  border-color: rgba(164, 57, 44, 0.22);
+}
+
 .s-ready {
   background: var(--open-bg);
   color: var(--open);
````

Run: `cd web && npm test && npm run build`
Expected: `Tests  18 passed (18)` and vite `✓ built`
Manual (if a stack is available: `docker compose up -d api` and `cd web && npm run dev`, http://localhost:5173 — needs `.env`; skip and say so otherwise): price line changes when "Require a password" toggles; a create shows "Publishing…" until it flips to Open; Memberbase "Set password" marks the row "✓ set"; the voting page of a password proposal asks for the password.

Step 3: commit
`git -c commit.gpgsign=false commit -m "feat(web): show publish states, price, end-date extension and member passwords" -- web/src/components/Proposals.jsx web/src/components/Memberbase.jsx web/src/components/AdminPage.jsx web/src/components/VotingPage.jsx web/src/styles.css`

Interfaces this task provides to later tasks: none (UI only).

### Task 9: e2e.sh, remove create-process.sh, docs
Model: standard   Repo: same   Files: e2e.sh, create-process.sh (deleted), README.md, CLAUDE.md, requests.http, .env.example
Goal: spec G; AC1, AC10, and the scripted half of AC11. `e2e.sh`: the managed-org cap is code 40184 in the 502 body (was `grep -q 40154` at `e2e.sh:76`), proposal create accepts 201 or 202 and polls `GET …/proposals/{id}` until `Open` (fails on `Failed`; base only accepts 201 at `:142`), new "Integrator status" step. Delete `create-process.sh` (calls `/census` and `/process`, removed upstream in #725). README (`:13-26` banner, `:75, 81-82` scopes, `:112-118` web app, `:132`, `:140-198` tests/scripts, `:202-209, 213, 235-238` notes, `:292-293, 305-312, 314, 319-321, 326-331` endpoints), CLAUDE.md (`:21, 25, 31, 35, 56-60, 62, 65, 75, 81-83, 108-115, 121-122, 125`), `requests.http` (`:44-50` features step out; price, integrator, password, end-date, retry, delete in), `.env.example` (`:4-5` scopes).

Step 1: write the failing check (AC1)
Run: `rg -n 'GetSubscriptionFeatures|/subscription|/plans|associations/[^ ]*/features|AssociationFeaturesResponse|create-process' src tests web/src README.md CLAUDE.md requests.http e2e.sh .env.example docker-compose.yml`
(without `rg`: `grep -rnE '<same pattern>' <same paths> --exclude-dir=bin --exclude-dir=obj`)
Expected: hits at `README.md:132, 179, 184-186, 213, 237-238, 292`, `CLAUDE.md:25, 83` and `requests.http:45` (src, tests and web/src were cleaned in Task 2)

Step 2: implement
`git rm create-process.sh`, then:

````diff
diff --git a/.env.example b/.env.example
index bc6ac98..1fba22c 100644
--- a/.env.example
+++ b/.env.example
@@ -1,8 +1,8 @@
 # Copy to .env and fill in. docker compose reads this automatically.
 ASPNETCORE_ENVIRONMENT=Development
 
-# Vocdoni SaaS API key for your integrator organization (Bearer token).
-# Needs the managed:write scope (managed:read too, for e2e.sh's adopt path).
+# Vocdoni SaaS API key for your integrator organization (Bearer token). Scopes: managed:write,
+# managed:read, quota:read (integrator status + wallet), voting:write, members:write.
 # Point at a saas-backend running the #571 multi-question /processes API (e.g. a local build).
 # NOTE: the demo API runs in a container, so a saas-backend on your host is NOT "localhost" — use
 # host.docker.internal (Docker Desktop). localhost:8080 would hit the demo's own server → 404s.
diff --git a/CLAUDE.md b/CLAUDE.md
index 26dc5d7..a45ebf4 100644
--- a/CLAUDE.md
+++ b/CLAUDE.md
@@ -18,21 +18,23 @@ not in this repo.
     Bearer token injected in `Program.cs`), `VocdoniModels.cs` wire DTOs.
   - `Services/Auth/` — JWT issuing. `Data/` — EF Core + SQLite, `DbSeeder` seeds the admin.
   - `Models/`, `Dtos/`, `Migrations/`.
-- `tests/HoaVoting.Tests/` — xUnit; `WebApplicationFactory` integration tests (11 today).
+- `tests/HoaVoting.Tests/` — xUnit: unit tests (`VocdoniClient` over a fake handler, `UpstreamErrors`,
+  mapping) plus controller tests that build controllers directly over SQLite in-memory and a scripted
+  `FakeVocdoniClient` (`ControllerTestBase`). No `WebApplicationFactory`.
 - `web/` — React 19 + Vite SPA, nginx in prod. `src/api.js` = the app API client (JWT bearer);
   `src/voting.js` = **client-side vote casting** via the integrator SDK; `src/status.js` = shared
   `isFinished`; one component per page under `src/components/`.
-- Roots: `e2e.sh`, `create-process.sh` (live-API scripts), `requests.http` (curl flow).
+- Roots: `e2e.sh` (live-API script), `requests.http` (curl flow).
 
 ## Commands
 
 ```bash
 docker compose up --build          # api :5095, web :3000 (proxies /api). Migrates + seeds on boot.
-dotnet test                         # 26 tests. No local .NET? run in a container:
+dotnet test                         # 85 tests. No local .NET? run in a container:
   docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
 dotnet test --filter <TestName>     # single test
 cd web && npm install && npm run dev # SPA hot reload :5173 (Vite proxies /api → :5095; needs api up)
-cd web && npm test                  # vitest — covers voting.js (batch/blind sign, relay, partial failure)
+cd web && npm test                  # vitest — covers voting.js (batch/blind sign, relay, partial failure, password hash)
 cd web && npm run build             # production bundle (also what the web image builds)
 ./e2e.sh                            # full live flow; VTYPE=single|multiple|ranked; needs .env creds
 ```
@@ -55,14 +57,24 @@ docker run --rm -v "$PWD":/src -w /src/src/HoaVoting.Api mcr.microsoft.com/dotne
 
 - **A proposal = a multi-question process.** `Proposal` (container) has many `ProposalQuestion`s
   (`Models/Proposal.cs`), each mapping 1:1 to an on-chain election. `ProposalsController.Create`
-  authors the whole process (`POST /processes`, census inline over the homeowners), publishes it as
-  one async **batch** (`POST /processes/{id}/publish` → poll `/jobs/{id}`), then reads it back
-  (`GET /processes/{id}`) to capture each question's on-chain `UpstreamId` + `Status`.
+  authors the whole process (`POST /processes`, census inline over the homeowners), **saves the row as
+  `Draft`**, then runs the publish routine `Services/ProposalSync.PublishAsync`: one async **batch**
+  (`POST /processes/{id}/publish` → job id stored on `Proposal.PublishJobId` → bounded ~40 s poll),
+  then reads it back (`GET /processes/{id}`) to capture each question's `UpstreamId` + `Status`.
+- **Publish states (pay-per-process, saas-backend #819).** Publishing debits the integrator wallet and
+  its job can stay pending for hours, so the local row records what upstream did — saved before the
+  call, and every save after an upstream side effect uses `CancellationToken.None`.
+  `ProposalStatus { Draft, Open, Closed, Failed }` (int, append only). Create answers 201 (`Open`), 202
+  (`Draft`, job still running) or 502. A refused publish deletes the upstream draft and the row; if
+  that delete fails the row is `Failed` + `PublishError`. A failed job → `Failed` (never auto-deleted:
+  upstream 409s 40903/40904 once elections were submitted). `DELETE …/proposals/{id}` (Draft/Failed
+  only) and `POST …/proposals/{id}/publish` (retry, Failed only; upstream adopts mined elections at no
+  new charge) resolve stuck rows. Limits: 15 questions, 90 days.
 - **Voting is client-side and batched; the backend never casts or signs.** `web/src/voting.js`
-  `castProcessVotes()` authenticates **once** per process (`client.processes.authStep0`), then signs the
+  `castProcessVotes()` authenticates **once** per process (`client.elections.authStep0`), then signs the
   whole ballot in **one** CSP call — the single fork in the flow (both branches return
   `[{upstreamId, signature, weight, code?, error?}]`, matched by `upstreamId`, never index):
-  `client.processes.signBatch` (saas-backend #634) for a regular census, or `signBlindCspBallots()`
+  `client.elections.signBatch` (saas-backend #634) for a regular census, or `signBlindCspBallots()`
   (blind CSP, #641 — see the anonymous bullet) for an anonymous one. One fresh `EphemeralSigner` per
   question, created *before* signing; envelopes built locally (`buildVoteTransaction`), then out in
   **one** `client.elections.voteBatch()` call (`POST /votes`, saas-backend #610) → **one** job. The
@@ -72,15 +84,29 @@ docker run --rm -v "$PWD":/src -w /src/src/HoaVoting.Api mcr.microsoft.com/dotne
   automatic re-sign — it burns the finite overwrite budget), and because a job fails if *any*
   envelope failed, the `jobs.waitFor()` call catches `JobFailedError` to read the outcomes off the
   failed job rather than reporting blanket failure. Bridge to the page: `VotingInfoResponse.ApiUrl`
-  + `ChainId` + `Anonymous`.
+  + `ChainId` + `Anonymous` + `RequirePassword` + `OrgAddress`. (`client.processes` is a deprecated
+  alias of `client.elections` since api-client 2.1.0 — use `elections`.)
 - **Anonymous voting (saas-backend #641, blind CSP).** `CreateProposalRequest.Anonymous` →
   `census.anonymous: true` on the process create → the census swaps to blind signatures
   (`OFF_CHAIN_CA_V2`): the CSP cannot link a voter to a ballot, and votes carry an
   `ECDSA_BLIND_PIDSALTED` proof (`proofType` on `buildVoteTransaction`; the CSP-pinned `weight` must
   be passed back verbatim — it salts the key the chain verifies). Auth and relay are unchanged; the
-  plain sign endpoints 400 on an anonymous census and vice versa. **Plan-gated**: publish fails
-  *async + opaquely* on plans without `features.anonymous`, so the admin form disables the toggle via
-  `GET api/associations/{id}/features` (→ `GET /organizations/{org}/subscription`).
+  plain sign endpoints 400 on an anonymous census and vice versa. On every plan since #718 (the
+  subscription/plan endpoints are gone, #855); a service-wide switch can still refuse it (400 40037),
+  which surfaces as a readable 502.
+- **Member password (saas-backend #760).** `RequirePassword` → census `authFields:
+  ["memberNumber","password"]`; members without one land in `missingData` (returned on create, not
+  stored) and are not added later. The password is hashed **in the browser** with
+  `hashMemberPassword(orgAddress, password)` (`@vocdoni/api-client`) — Memberbase sends the hash to
+  `PUT …/homeowners/{memberId}/password` (the controller checks the member exists first: upstream's
+  upsert creates a phantom member for an unknown id), and the voting page sends it straight to step 0.
+  The backend accepts only `^[0-9a-f]{64}$` and never stores or logs it. A wrong password answers
+  40802 like an unknown member; step 0 is throttled (40017).
+- **Pricing + wallet (saas-backend #819).** `GET api/associations/{id}/price` → `GET /pricing?voters=N`
+  (0 voters answered locally: upstream needs ≥ 1). `GET api/integrator` (admin) → `GET /integrator` +
+  the wallet of the integrator org, whose address is the `managedBy` of its first managed org.
+  Upstream billing/limit codes (40175, 40183, 40184, 40176) become readable sentences in the 502
+  `detail` (`Services/Vocdoni/UpstreamErrors.cs`).
 - **Voting kinds are a cross-cut across four places.** `VotingType`
   (`single|multiple|ranked|cumulative`, `Models/VotingType.cs`, JSON as a camelCase string via
   `JsonStringEnumConverter` in `Program.cs`) → a **named** #638 type in
@@ -105,23 +131,30 @@ docker run --rm -v "$PWD":/src -w /src/src/HoaVoting.Api mcr.microsoft.com/dotne
   `VotingController` deliberately drops them (never public). Only single-choice is supported: the
   backend correlates each vote's selected value with the open choice's value, which only matches
   `votes = [chosenIndex]`.
-- **Async everything via jobs.** Publish, question-status change, vote relay, bulk member add return a
-  `jobId`; the client polls `GET /jobs/{id}` (fail-fast on `failed`). Publish is idempotent (200).
+- **Async everything via jobs.** Publish, end-date change, question-status change, vote relay, bulk
+  member add return a `jobId`. `VocdoniClient.WaitForJobAsync` is a bounded poll (~40 s, under nginx's
+  60 s proxy timeout) returning `Completed | Failed | Pending`; what outlives it is settled by the
+  read-side reconcile. Publish is idempotent (200). End date: `PUT …/proposals/{id}/end-date` →
+  `PUT /processes/{id}/endDate` (saas-backend #844), Open proposals only, extend only.
 - **Status + tally reconcile.** `ProposalsController` List/Get (`HydrateAsync`) + `VotingController`
-  read `GET /processes/{id}` (the process read carries per-question live on-chain `status` always, plus
-  an inline `results` tally — `voteCount` + `maxVoters` + `results` matrix — resolved only once a
-  question hits **`results`** status, saas-backend #596). They refresh each question's `Status`, mark
-  the proposal `Closed` when all questions ended, and pass the inline tallies into the response (matched
-  by `UpstreamId`). Tallies render via `web/src/tally.js` `tallyCounts(results, kind)` +
+  share `ProposalSync.ReconcileAsync` over `GET /processes/{id}` (the process read carries per-question
+  live on-chain `status` always, plus an inline `results` tally — `voteCount` + `maxVoters` + `results`
+  matrix — resolved only once a question hits **`results`** status, saas-backend #596). In order: a
+  `Draft` is promoted to `Open` only by a read that says `published` with every `upstreamId` present,
+  or failed from its stored job; upstream `endDate` is copied; question `Status` refreshed; an `Open`
+  proposal (only) is marked `Closed` when all questions ended or its end date passed. It never deletes;
+  its save tolerates a row deleted meanwhile. The public endpoint 404s `Draft`/`Failed`. Inline
+  tallies go into the response (matched by `UpstreamId`). Tallies render via `web/src/tally.js` `tallyCounts(results, kind)` +
   `QuestionResults.jsx` (single=`results[0]`, multiple=`results[i][1]`, ranked and cumulative =
   index-weighted `Σ results[i][v]·v`; `maxVoters` is the per-question turnout denominator). The public page also derives finished via
   `isFinished`. (The separate `GET /processes/{id}/results` endpoint exists but is unused — its shape
   dropped `status`, which we need every read.)
-- **Auth-only census.** Voters authenticate by **member number** (no 2FA); the process census is
-  inline (`census: { authFields: ["memberNumber"], memberIds }`) — no separate census/group/publish.
+- **Auth-only census.** Voters authenticate by **member number** (plus password when required; no
+  2FA); the process census is inline (`census: { authFields, memberIds }`) — no separate
+  census/group/publish.
 - **`chainId` comes from the process (#582).** The process read (`GET /processes/{id}`) carries the
-  Vochain `chainId` votes must be signed against. `ProposalsController.Create` captures it from the
-  publish read-back onto `Proposal.ChainId`; `VotingController` exposes it to the page for vote
+  Vochain `chainId` votes must be signed against. the publish routine captures it from the
+  read-back onto `Proposal.ChainId`; `VotingController` exposes it to the page for vote
   signing. (There is no `Vocdoni:ChainId` config anymore.)
 
 ## Conventions
diff --git a/README.md b/README.md
index 8c2fab2..3f9f380 100644
--- a/README.md
+++ b/README.md
@@ -9,20 +9,16 @@ creates **proposals**, and reads voting **results**. Built on the Vocdoni SaaS A
 >  - They now live on the Vocdoni **[developer portal](https://developer.vocdoni.io)**, start there for concepts, guides, and the end-to-end flow.
 >  - For the exact endpoints and payloads, check the **[SaaS API swagger](https://github.com/vocdoni/saas-backend/blob/main/docs/swagger.yaml)**.
 
-> [!WARNING]
-> **This branch integrates the multi-question `/processes` API from [saas-backend #571](https://github.com/vocdoni/saas-backend/pull/571) (unmerged).**
-> A **proposal is now a voting process with N questions**, each its own on-chain election; the legacy
-> singular `/process` + `/process/bundle` flow was removed. #571 is **deployed nowhere yet** — run a
-> local saas-backend on the `feat/processes-api` branch and set `VOCDONI_SERVER_URL` to point at it.
-> Voting is per-question (CSP sign + relay client-side); per-question tallies (`voteCount`, `maxVoters`
-> turnout, `results` matrix) come inline on the process read `GET /processes/{id}` once a question
-> reaches `results` status ([saas-backend #596](https://github.com/vocdoni/saas-backend/pull/596)), and
-> the `chainId` needed to sign votes also comes from the process read
-> ([saas-backend #582](https://github.com/vocdoni/saas-backend/pull/582)).
-> A single-choice question may also carry an open **"Other" choice** whose voters attach a free-text
-> **memo** ([saas-backend #577](https://github.com/vocdoni/saas-backend/pull/577), **unmerged** — run
-> `feat/vote-memo`); the memos surface to the org owner inline on the process read (manager-only, once
-> `results`), never to the public voting page.
+> [!NOTE]
+> **Needs a saas-backend that includes [#760](https://github.com/vocdoni/saas-backend/pull/760) (member
+> password), [#819](https://github.com/vocdoni/saas-backend/pull/819) (pay-per-process billing),
+> [#844](https://github.com/vocdoni/saas-backend/pull/844) (extend end date) and
+> [#855](https://github.com/vocdoni/saas-backend/pull/855) (plan endpoints removed)** — all on `main`.
+> A **proposal is a voting process with N questions** (#571), each its own on-chain election. Voting is
+> client-side (CSP sign + batch relay); per-question tallies come inline on the process read
+> `GET /processes/{id}` once a question reaches `results` (#596), along with the `chainId` (#582). A
+> single-choice question may carry an open **"Other" choice** whose voters attach a free-text memo
+> (#577), shown to the owner only.
 
 ## Architecture
 
@@ -69,7 +65,7 @@ Admin:Email / Admin:Password       seeds the admin on startup
 Vocdoni:ServerUrl                  SaaS base URL the backend calls (dev: https://saas-api-dev.vocdoni.net;
                                    stg: https://saas-api-stg.vocdoni.net)
 Vocdoni:BrowserUrl                 browser-facing SaaS URL for the voting page (defaults to ServerUrl)
-Vocdoni:ApiToken                   integrator org's API key (Bearer); needs the managed:write scope
+Vocdoni:ApiToken                   integrator org's API key (Bearer); scopes below
 ConnectionStrings:Default          SQLite by default
 ```
 
@@ -78,8 +74,11 @@ address is configured.
 
 **Prerequisites:**
 1. A Vocdoni integrator account (free tier, SaaS dashboard)
-2. An **API key** minted under that org (in the dashboard) with the **`managed:write`** scope (and
-   `managed:read` if you run `e2e.sh`, which lists managed orgs for its adopt path)
+2. An **API key** minted under that org (in the dashboard) with the scopes **`managed:write`**,
+   **`managed:read`**, **`quota:read`** (integrator status + wallet), **`voting:write`** and
+   **`members:write`**
+3. A **topped-up integrator wallet** (in the Vocdoni portal) once you need more than one association
+   or a proposal over 25 voters: publishing is pay-per-process and paid from that wallet
 
 ```bash
 cd src/HoaVoting.Api
@@ -109,16 +108,20 @@ A React (Vite) SPA lives in `web/`, served by its own **`web`** compose service
 **http://localhost:3000**. nginx serves the SPA and proxies `/api` → the `api` service, so the
 browser stays same-origin (no CORS). `docker compose up --build` brings up both services. Two roles:
 
-- **Backend admin** (`SuperAdmin`) — create and list associations.
-- **Association admin** (`Owner`) — manage the **memberbase** (add/remove homeowners + CSV import)
-  and **voting processes** (create a **single choice**, **multiple choice**, **ranked**, or
-  **cumulative/quadratic** ballot; optionally **anonymous** when the plan allows it; view results,
-  close; edit each question's **voter eligibility** live). Voters authenticate by member number
-  (no 2FA). Each proposal exposes a **Voting page** link. Results bars fill against the **census
-  size** (turnout share, or the top score for ranked/cumulative) and show the eligible count.
+- **Backend admin** (`SuperAdmin`) — create and list associations; sees the integrator **wallet
+  balance** and whether another association can be created before a top-up.
+- **Association admin** (`Owner`) — manage the **memberbase** (add/remove homeowners + CSV import,
+  set a homeowner's **password**) and **voting processes** (create a **single choice**, **multiple
+  choice**, **ranked**, or **cumulative/quadratic** ballot; optionally **anonymous** and/or
+  **password-protected**; see the **estimated price** before creating; view results, close,
+  **extend the end date**; edit each question's **voter eligibility** live; **retry** or **delete** a
+  proposal whose publish failed or is stuck). Voters authenticate by member number (no 2FA), plus
+  their password when the proposal requires one. Each live proposal exposes a **Voting page** link.
+  Results bars fill against the **census size** (turnout share, or the top score for
+  ranked/cumulative) and show the eligible count.
 - **Public voting page** — `/processes/{processId}` is a no-login page (modeled on
   app.vocdoni.io's `/processes/:id`). It shows the ballot and lets a homeowner **cast a vote**:
-  authenticate by member number, then pick one choice (single), several (multiple), **drag to
+  authenticate by member number (and password, hashed in the browser), then pick one choice (single), several (multiple), **drag to
   rank** the options (ranked), or **distribute credits** (cumulative/quadratic), and submit. Casting
   runs entirely client-side against the SaaS API via `@vocdoni/integrator-sdk` (see
   `web/src/voting.js`) — CSP auth → sign the whole ballot in **one call** (`sign-batch`, or the
@@ -129,7 +132,7 @@ browser stays same-origin (no CORS). `docker compose up --build` brings up both
 | Service | URL | Purpose |
 |---------|-----|---------|
 | `web`   | http://localhost:3000 | SPA (UI + public voting page) + `/api` proxy |
-| `api`   | http://localhost:5095 | REST API directly (used by `e2e.sh`, `create-process.sh`, dev proxy) |
+| `api`   | http://localhost:5095 | REST API directly (used by `e2e.sh`, dev proxy) |
 
 Local development with hot reload (Vite proxies `/api` → `:5095`, so keep the backend running):
 
@@ -140,7 +143,7 @@ cd web && npm install && npm run dev   # http://localhost:5173
 
 ## Test
 
-**Unit tests** (26/26 pass):
+**Backend tests** (85/85 pass):
 
 ```bash
 docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
@@ -150,13 +153,20 @@ Or locally: `dotnet test`.
 
 - `AuthorizationTests` — an Owner cannot access another's association.
 - `VocdoniClientTests` — Bearer token is sent and failures surface without retry; member listing
-  walks every page; async publish/status poll the job endpoint (202→poll, 200→idempotent, fail-fast);
-  question-census updates (200 vs 202+job vs 409), subscription features, `census.anonymous` on the wire.
+  walks every page; publish returns its job without polling (200 → null); bounded job wait
+  (completed / failed / pending); end date as RFC 3339 UTC; pricing, integrator, wallet and member
+  upsert on the wire; question-census updates (200 vs 202+job vs 409), `census.anonymous`.
 - `QuestionMappingTests` — the demo kind → named #638 question type mapping (ranked sends no
   typeSetup; cumulative carries budget + costExponent) and the 40173 eligibility-conflict parse.
+- `UpstreamErrorsTests` — readable 502 details for the billing/limit codes.
+- `MigrationTests` — migrations cover the model (EF refuses pending changes).
+- Controller tests over SQLite in-memory + `FakeVocdoniClient` (`ControllerTestBase`):
+  `ProposalLifecycleTests` (resilient create, reconcile, delete/retry/close, end date) and
+  `BillingAndPasswordTests` (price, integrator wallet, member password hash).
 
-**Web tests** (15/15 pass, vitest): `cd web && npm test` — `castProcessVotes` orchestration: one
-batch sign (plain or blind), upstreamId matching, per-ballot sign failures, one relay, partial failure.
+**Web tests** (18/18 pass, vitest): `cd web && npm test` — `castProcessVotes` orchestration: one
+batch sign (plain or blind), upstreamId matching, per-ballot sign failures, one relay, partial
+failure, and the member password hashed with the org address before step 0.
 
 **End-to-end test** against the live Vocdoni SaaS API:
 
@@ -167,50 +177,50 @@ VTYPE=ranked ./e2e.sh     # voting type for the proposal: single (default) | mul
 ```
 
 Requires `.env` with valid Vocdoni credentials. The script:
-- Creates a fresh association, reuses an existing one, or **adopts** an existing managed org if
-  the integrator quota is full (`POST /api/associations/import`).
+- Prints the integrator status (`GET /api/integrator`: wallet balance, whether another association
+  can be created).
+- Creates a fresh association, reuses an existing one, or **adopts** an existing managed org when
+  the integrator must top up before creating another (402 code 40184; `POST /api/associations/import`).
 - Loads the **memberbase** from a CSV (`First Name,Member Number` or `First Name,Email,Member
   Number`) as homeowners — idempotent (skips if members already exist).
-- Creates a proposal and waits for the async publish (~10–30s).
+- Creates a proposal; on `202` (publish still running) it polls the proposal until `Open` (fails on
+  `Failed`, gives up after 15 min).
 
 The proposal always uses a **CSP census** where voters authenticate by **member number** alone (no
-2FA). Set `VTYPE` to create a single-choice (default), multiple-choice, or ranked ballot.
-
-**Create a process on an existing census** (`create-process.sh`) — skips all setup
-(org/owner/members) and just creates + publishes a new voting process on a census that already
-exists. Talks directly to the Vocdoni API with the integrator token:
-
-```bash
-./create-process.sh                                   # discover org + last census from the app
-ORG=0x.. CENSUS_ID=6a.. ./create-process.sh           # standalone (existing integrator org)
-TITLE="Budget 2026" ./create-process.sh
-```
-
-Prints the new ProcessID (used for status/results) and its on-chain election id. Multiple processes
-can share one census.
-
-**How auth-only publishing works:** the plain `POST /census/{id}/publish` only accepts the 2FA
-census types (`mail`/`sms`/`sms_or_mail`) and rejects auth-only (member-number) censuses with
-`census type not found`. The backend instead publishes via a **member group** (`POST
-/census/{id}/group/{groupid}/publish`), which supports auth-only censuses and populates participants
-from the group. Note: each `Member Number` must be **unique** (the voting credential is
-`hash(memberNumber)`) — duplicate member numbers fail at publish.
+2FA). Set `VTYPE` to create a single-choice (default), multiple-choice, or ranked ballot. Each
+`Member Number` must be **unique** (the voting credential is derived from it).
 
 ## Implementation Notes
 
-- **Async publish:** publish and status-change return `202 + jobId`. `PublishVotingProcessAsync`
-  polls `GET /jobs/{jobId}` until the job completes (failing fast on a failed job); publish is
-  idempotent, so an already-published process returns `200` directly. `SetQuestionsStatusAsync`
-  deliberately does **not** poll — closing a proposal is fire-and-forget, and the next process read
-  reconciles the real status. Marked `ponytail:` — for production, move the poll to a background worker.
-- **Integrator quota:** The free tier allows **1 managed organization**. Multiple associations
-  require additional quota or a new integrator account. Deleting an association now frees the slot
-  (`DELETE /integrator/organizations/{addr}` rolls back the integrator's usage counters).
+- **Publish states:** publish returns `202 + jobId` (or `200` when already published). Pay-per-process
+  publish jobs can stay pending from minutes to hours (the service retries transient chain failures),
+  so the proposal row is saved as `Draft` **before** publishing and the job id is stored as soon as it
+  is known. The create request waits a bounded ~40 s (`WaitForJobAsync`, under nginx's 60 s proxy
+  timeout): `201 Open` if the job completed, `202 Draft` if it is still running, `502` if it failed.
+  A refused publish deletes the upstream draft (and the row); if upstream will not delete it the row
+  becomes `Failed`. A failed job always leaves a `Failed` row with `publishError` — upstream refuses
+  to delete a draft whose publish submitted elections (409 40903/40904) and publishing it again
+  adopts them at no new charge, hence **Retry publish**. Every list/get (and the public page) runs the
+  same reconcile: promote a `Draft` whose process read is `published` with every question's
+  `upstreamId`, or fail it from its job. `SetQuestionsStatusAsync` deliberately does **not** poll —
+  closing is fire-and-forget. Marked `ponytail:` — for production, move the polls to a background worker.
+- **Pay-per-process billing (saas-backend #819):** publishing debits the integrator's wallet (free up
+  to 25 voters). Refusals surface as readable 502 details: 40175 wallet short (with the amounts), 40183
+  spend cap, 40176 over 50 000 voters. The owner sees the estimate first (`GET /pricing`, via
+  `GET /api/associations/{id}/price`). Until the wallet holds a top-up the integrator may create only
+  one managed org (402 40184). Limits: 15 questions per process, 90-day processes, 20 drafts per org.
+- **Member password (saas-backend #760):** a proposal with `requirePassword` sets census
+  `authFields: ["memberNumber","password"]`; homeowners without a password are left out (`missingData`
+  on the create response) and are not added if one is set later. Passwords are hashed **in the
+  browser** with `hashMemberPassword(orgAddress, password)` from `@vocdoni/api-client` (needs HTTPS or
+  localhost); the backend only forwards the 64-hex hash and never stores it. A wrong password answers
+  like an unknown member (40802); step 0 is throttled (40017).
+- **Extend end date (saas-backend #844):** `PUT /processes/{id}/endDate` enqueues a job; the demo
+  saves the new end once it completed, and otherwise picks upstream's `endDate` up on the next read.
 - **Org/process addresses:** Sent/read as **hex strings** (Vocdoni's `HexBytes` wire format),
   not the int arrays the swagger nominally shows.
 - **Swagger drift:** member deletion is `DELETE /organizations/{address}/members` (**plural**);
   the swagger's singular `/member` returns 404 on the deployed backend.
-- **Census reuse:** multiple processes can target the same published census (see `create-process.sh`).
 - **Client-side voting:** the web app casts ballots in the browser via `@vocdoni/integrator-sdk`
   (`@vocdoni/api-client` + `@vocdoni/api-voting`), which talk **only** to the SaaS API. The flow lives
   in `web/src/voting.js` (`castProcessVotes`): CSP auth **once** per process (member number) → sign
@@ -232,10 +242,9 @@ from the group. Note: each `Member Number` must be **unique** (the voting creden
   (`blind-point`), blind the bundle locally, get it signed (`blind-sign`), unblind — and the envelope
   carries an `ECDSA_BLIND_PIDSALTED` proof. The CSP-pinned `weight` must be passed back **verbatim**
   (it salts the key the chain verifies). Auth and the batch relay are unchanged; the plain sign
-  endpoints reject an anonymous census and vice versa. **Plan-gated:** publishing an anonymous
-  process on a plan without `features.anonymous` fails asynchronously with an opaque job error, so
-  the admin form disables the toggle based on `GET /api/associations/{id}/features`
-  (→ `GET /organizations/{org}/subscription`).
+  endpoints reject an anonymous census and vice versa. Available on every plan (#718); a service-wide
+  switch can refuse it at create (400 40037 "anonymous voting is temporarily unavailable"), shown
+  verbatim.
 - **Live question eligibility (saas-backend #621):** a published question's voter subset can be
   edited via `PUT /processes/{pid}/questions/{qid}/census` with the **complete** desired member-id
   list (`[]` reopens it to the whole census; the response reports `eligible/added/removed`, and a
@@ -289,8 +298,11 @@ All endpoints except `/api/auth/login` require a valid JWT bearer token.
 - `POST /api/associations/import` — adopt an existing managed org (no Vocdoni create call)
 - `GET /api/associations` — list all
 - `GET /api/associations/{id}` — get one (admin or its owner)
-- `GET /api/associations/{id}/features` — plan features for the admin UI (currently
-  `anonymousVoting`, from the org's Vocdoni subscription)
+- `GET /api/associations/{id}/price?requirePassword=bool` — estimated pay-per-process price over the
+  current homeowners (only those with a password when `requirePassword`): `{voters, totalCents,
+  currency, quoteRecommended, quoteRequired}` (admin or its owner)
+- `GET /api/integrator` — `{canCreateManagedOrg, managedOrgsCreated, balanceCents?, currency?}`; the
+  balance is null when unknown (no managed org yet, or the key lacks `quota:read`)
 - `DELETE /api/associations/{id}` — remove the association + its proposals + owner login from the
   app, **and** delete the Vocdoni managed org via `DELETE /integrator/organizations/{addr}` (cascade:
   members, censuses, processes, bundles), reclaiming integrator quota. Returns **409** if the org
@@ -299,33 +311,43 @@ All endpoints except `/api/auth/login` require a valid JWT bearer token.
 
 **Homeowners (admin or association owner):**
 - `GET /api/associations/{id}/homeowners` — list members
-- `POST /api/associations/{id}/homeowners` — add member (hits Vocdoni)
+- `POST /api/associations/{id}/homeowners` — add member (hits Vocdoni); optional `passwordHash`
+- `PUT /api/associations/{id}/homeowners/{memberId}/password` — `{passwordHash}` (64 lowercase hex,
+  computed in the browser); 404 unless `memberId` is a current member
 - `DELETE /api/associations/{id}/homeowners/{memberId}` — remove member
 
 **Proposals (admin or association owner):**
 - `POST /api/associations/{id}/proposals` — create a **multi-question voting process** in one call
-  (`POST /processes` with the census inline over the homeowners → `POST /processes/{id}/publish`,
-  polled → read back to capture each question's on-chain `upstreamId`, `status` and the `chainId`).
-  There is no group/census/bundle dance. Body: `title`, `description`, `startDate`, `endDate`,
-  `anonymous` (blind-CSP voting, plan-gated), and `questions[]`, each
+  (`POST /processes` with the census inline over the homeowners → local `Draft` row →
+  `POST /processes/{id}/publish` → bounded job wait → read back to capture each question's on-chain
+  `upstreamId`, `status` and the `chainId`). **201** `Open`, **202** `Draft` (still publishing), or
+  **502** (refused / job failed). Body: `title`, `description`, `startDate`, `endDate` (≤ 90 days),
+  `anonymous` (blind-CSP voting), `requirePassword`, and `questions[]` (≤ 15), each
   `{ title, choices[], kind, budget?, costExponent? }` with `kind` = `single` (default) | `multiple` |
-  `ranked` | `cumulative` (which requires `budget` ≥ 1 and `costExponent` 1|2). Voters always
-  authenticate by member number (no 2FA).
+  `ranked` | `cumulative` (which requires `budget` ≥ 1 and `costExponent` 1|2). The response carries
+  `missingData` (homeowners left out of the census) and, on `Failed`, `publishError`.
 - `GET /api/associations/{id}/proposals` — list
 - `GET /api/associations/{id}/proposals/{pid}` — get one
-- `POST /api/associations/{id}/proposals/{pid}/close` — end voting (all questions)
+- `POST /api/associations/{id}/proposals/{pid}/close` — end voting (all questions); 409 unless `Open`
+- `PUT /api/associations/{id}/proposals/{pid}/end-date` — `{endDate}`, later than the current one,
+  `Open` only: 200 done, 202 still running on chain, 502 failed
+- `POST /api/associations/{id}/proposals/{pid}/publish` — retry the publish of a `Failed` proposal
+  (409 otherwise)
+- `DELETE /api/associations/{id}/proposals/{pid}` — delete a `Draft` or `Failed` proposal and its
+  upstream draft (409 otherwise; 502 and the row stays when upstream refuses)
 - `PUT /api/associations/{id}/proposals/{pid}/questions/{qid}/eligibility` — replace the question's
   voter subset on the live process (`{memberIds}` = complete list, `[]` = everyone); 409 with
   `signedMemberIds` when a voter who already holds a ballot signature would lose eligibility
 
-  List and get both hydrate from `GET /processes/{id}`: they refresh each question's live status,
-  mark the proposal closed once every question has ended, and inline the per-question tally — so
-  there is no separate results endpoint.
+  List and get both reconcile from `GET /processes/{id}`: they settle `Draft` proposals, copy the
+  upstream end date, refresh each question's live status, mark an `Open` proposal closed once every
+  question has ended, and inline the per-question tally — so there is no separate results endpoint.
 
 **Public (no auth):**
 - `GET /api/processes/{processId}` — voting-page data for the public `/processes/{processId}` page:
-  title, description, dates, status, `anonymous`, and one entry per question (`title`, `choices[]`,
+  title, description, dates, status, `anonymous`, `requirePassword`, `orgAddress` (for the password
+  hash), and one entry per question (`title`, `choices[]`,
   `kind`, `budget`/`costExponent` for cumulative, `upstreamId`, `status`, and best-effort on-chain
   `voteCount`, `maxVoters`, `results`). Also returns `apiUrl` (the SaaS API base URL) and `chainId` —
   the things the page cannot derive — so it can sign (batch or blind) and batch-relay votes
-  client-side via the integrator SDK.
+  client-side via the integrator SDK. 404 while the proposal is `Draft` or `Failed`.
diff --git a/e2e.sh b/e2e.sh
index c7d0cab..584f915 100755
--- a/e2e.sh
+++ b/e2e.sh
@@ -57,7 +57,16 @@ step "Guard: unauthenticated create → 401"
 req POST /api/associations "{\"name\":\"x\",\"ownerEmail\":\"x@x\",\"ownerPassword\":\"x\"}"
 [ "$HTTP" = 401 ] && ok "401" || bad "expected 401, got $HTTP"
 
-# --- 3. find-or-create association (integrator free plan caps managed orgs, so reuse) ------
+# --- 2b. integrator wallet + managed-org cap (saas-backend #819) -------------
+step "Integrator status"
+req GET /api/integrator "" "$ADMIN_TOKEN"
+if [ "$HTTP" = 200 ]; then
+  ok "canCreateManagedOrg=$(j "$BODY" .canCreateManagedOrg), managedOrgsCreated=$(j "$BODY" .managedOrgsCreated), balance=$(j "$BODY" '.balanceCents // "unavailable"') $(j "$BODY" '.currency // ""')"
+else
+  bad "integrator HTTP $HTTP — $BODY (the API key needs quota:read)"
+fi
+
+# --- 3. find-or-create association (managed-org creation is capped until the wallet is topped up, so reuse) ------
 # This e2e owns its test associations and always uses the same owner password, so a reused
 # association can be logged into. A fresh account creates one; later runs reuse it.
 step "Find or create association"
@@ -73,8 +82,9 @@ else
   if [ "$HTTP" = 201 ]; then
     ASSOC_ID=$(j "$BODY" .id); ORG=$(j "$BODY" .vocdoniOrgAddress)
     ok "created association id=$ASSOC_ID, vocdoni org=$ORG"
-  elif printf '%s' "$BODY" | grep -q 40154; then
-    # Integrator managed-org quota is full — adopt an existing managed org instead.
+  elif [ "$(j "$BODY" .code)" = 40184 ]; then
+    # Upstream 402 (40184, surfaced by the API as 502 + code): no more managed orgs until the integrator
+    # wallet is topped up — adopt an existing managed org instead.
     ORG=$(curl -s -m 15 "$VOCDONI_SERVER_URL/integrator/organizations" \
       -H "Authorization: Bearer $VOCDONI_API_TOKEN" | jq -r '.organizations[0].address // empty')
     [ -n "$ORG" ] || die "managed-org quota full and no existing org to adopt"
@@ -133,17 +143,31 @@ COUNT=$(printf '%s' "$BODY" | jq 'length' 2>/dev/null)
 [ "$HTTP" = 200 ] && ok "200, count=$COUNT" || bad "list HTTP $HTTP — $BODY"
 
 # --- 8. owner creates a multi-question proposal (Vocdoni #571: authored + batch-published) ------
-# Publish is an async on-chain batch (one election per question); this step waits for it (~10-30s).
+# Publish is an async on-chain batch (one election per question), paid from the integrator wallet
+# (free up to 25 voters, saas-backend #819). The API waits ~40 s: 201 = Open, 202 = still publishing,
+# in which case this polls the proposal until the read-side reconcile reports Open (or Failed).
 START=$(date -u +%Y-%m-%dT%H:%M:%SZ)
 END=$(date -u -v+7d +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d '+7 days' +%Y-%m-%dT%H:%M:%SZ)
 step "Create proposal (2 questions: single + multiple; waits for batch publish)"
 req POST "/api/associations/$ASSOC_ID/proposals" \
   "{\"title\":\"Building decisions\",\"description\":\"E2E proposal\",\"startDate\":\"$START\",\"endDate\":\"$END\",\"questions\":[{\"title\":\"Repaint the lobby?\",\"kind\":\"single\",\"choices\":[{\"title\":\"Yes\"},{\"title\":\"No\"}]},{\"title\":\"Which upgrades?\",\"kind\":\"multiple\",\"choices\":[{\"title\":\"Elevator\"},{\"title\":\"Garden\"},{\"title\":\"Gym\"}]}]}" "$OWNER_TOKEN"
-if [ "$HTTP" = 201 ]; then
-  PROP_ID=$(j "$BODY" .id); PROC=$(j "$BODY" .vocdoniProcessId)
-  QN=$(printf '%s' "$BODY" | jq '.questions | length'); UP=$(printf '%s' "$BODY" | jq -r '.questions[0].upstreamId')
-  ok "201 — proposal id=$PROP_ID, process=$PROC, $QN questions, q1 upstreamId=${UP:0:12}…"
-  [ -n "$UP" ] && [ "$UP" != "null" ] && ok "questions published (have on-chain ids)" || bad "questions missing upstreamId — batch publish incomplete"
+if [ "$HTTP" = 201 ] || [ "$HTTP" = 202 ]; then
+  PROP_ID=$(j "$BODY" .id); PROC=$(j "$BODY" .vocdoniProcessId); STATUS=$(j "$BODY" .status)
+  ok "$HTTP — proposal id=$PROP_ID, process=$PROC, status=$STATUS"
+  for _ in $(seq 1 90); do   # up to 15 min: a stalled chain keeps the publish job pending
+    [ "$STATUS" = Draft ] || break
+    sleep 10
+    req GET "/api/associations/$ASSOC_ID/proposals/$PROP_ID" "" "$OWNER_TOKEN"
+    STATUS=$(j "$BODY" .status)
+  done
+  case "$STATUS" in
+    Open)
+      QN=$(printf '%s' "$BODY" | jq '.questions | length'); UP=$(printf '%s' "$BODY" | jq -r '.questions[0].upstreamId')
+      ok "Open — $QN questions, q1 upstreamId=${UP:0:12}…"
+      [ -n "$UP" ] && [ "$UP" != "null" ] && ok "questions published (have on-chain ids)" || bad "questions missing upstreamId — batch publish incomplete" ;;
+    Failed) bad "publish failed: $(j "$BODY" .publishError)"; PROP_ID="" ;;
+    *) bad "proposal still $STATUS after 15 min"; PROP_ID="" ;;
+  esac
 else
   bad "create proposal HTTP $HTTP — $BODY"; PROP_ID=""
 fi
diff --git a/requests.http b/requests.http
index e9b6717..76ac129 100644
--- a/requests.http
+++ b/requests.http
@@ -41,13 +41,28 @@ Authorization: Bearer {{ownerToken}}
 GET {{host}}/api/associations/{{assocId}}/homeowners
 Authorization: Bearer {{ownerToken}}
 
-### 6. Owner checks the plan features (gates the anonymous toggle)
-GET {{host}}/api/associations/{{assocId}}/features
+### 5b. Owner sets a homeowner's password. passwordHash is the browser-side
+# hashMemberPassword(orgAddress, password) from @vocdoni/api-client — 64 lowercase hex, never the
+# password itself. 404 unless the id is a current member.
+PUT {{host}}/api/associations/{{assocId}}/homeowners/MEMBER_ID/password
+Content-Type: application/json
+Authorization: Bearer {{ownerToken}}
+
+{ "passwordHash": "0000000000000000000000000000000000000000000000000000000000000000" }
+
+### 6. Owner checks the estimated price (pay-per-process, paid from the integrator wallet; free up to 25 voters)
+GET {{host}}/api/associations/{{assocId}}/price?requirePassword=false
 Authorization: Bearer {{ownerToken}}
 
+### 6b. Admin checks the integrator wallet and whether another association can be created
+GET {{host}}/api/integrator
+Authorization: Bearer {{adminToken}}
+
 ### 7. Owner creates a multi-question proposal (Vocdoni: POST /processes -> publish -> read back)
 # kind = single | multiple | ranked | cumulative (budget + costExponent 1|2).
-# anonymous = blind-CSP voting (plan-gated — see step 6).
+# anonymous = blind-CSP voting; requirePassword = voters also enter their password.
+# 201 + status Open when the publish job finished within ~40 s; 202 + status Draft while it is still
+# running (re-read until Open or Failed); missingData = homeowners left out (e.g. no password).
 # @name createProposal
 POST {{host}}/api/associations/{{assocId}}/proposals
 Content-Type: application/json
@@ -57,6 +72,7 @@ Authorization: Bearer {{ownerToken}}
   "title": "Annual assembly",
   "description": "Two decisions for this year.",
   "anonymous": false,
+  "requirePassword": false,
   "startDate": "2026-06-24T00:00:00Z",
   "endDate": "2026-07-01T00:00:00Z",
   "questions": [
@@ -90,6 +106,21 @@ Authorization: Bearer {{ownerToken}}
 
 { "memberIds": [] }
 
-### 10. Close voting
+### 10. Extend the end date of an open proposal (200 done, 202 still running on chain)
+PUT {{host}}/api/associations/{{assocId}}/proposals/{{proposalId}}/end-date
+Content-Type: application/json
+Authorization: Bearer {{ownerToken}}
+
+{ "endDate": "2026-07-08T00:00:00Z" }
+
+### 11. Close voting (Open proposals only, else 409)
 POST {{host}}/api/associations/{{assocId}}/proposals/{{proposalId}}/close
 Authorization: Bearer {{ownerToken}}
+
+### 12. Retry the publish of a Failed proposal (adopts already-mined elections at no new charge)
+POST {{host}}/api/associations/{{assocId}}/proposals/{{proposalId}}/publish
+Authorization: Bearer {{ownerToken}}
+
+### 13. Delete a proposal stuck publishing (Draft) or Failed — deletes the upstream draft too (409 otherwise)
+DELETE {{host}}/api/associations/{{assocId}}/proposals/{{proposalId}}
+Authorization: Bearer {{ownerToken}}
````

Run: the `rg` command above, then `bash -n e2e.sh && test ! -e create-process.sh && echo ok`, then both suites
Expected: `rg` prints nothing (exit 1); `ok`; `Passed!  - Failed:     0, Passed:    85, …`; `Tests  18 passed (18)`

Step 3: commit
`git -c commit.gpgsign=false commit -m "docs(readme): describe billing, publish states, passwords and end-date extension" -- e2e.sh create-process.sh README.md CLAUDE.md requests.http .env.example`

Interfaces this task provides to later tasks: none.

## Merge and release
1. Full suites on `feature/saas-demo` HEAD:
   `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test` → `Passed!  - Failed:     0, Passed:    85, Skipped:     0, Total:    85`;
   `cd web && npm ci && npm test && npm run build` → `Tests  18 passed (18)`, vite `✓ built`;
   AC1 `rg` (or the grep fallback, Task 9) → no output; `bash -n e2e.sh` → no output; `create-process.sh` absent.
2. Merge order: one PR `feature/saas-demo` → `main` in `vocdoni/saas-integrator-demo`, body links the issue the controller opens (`Closes #<issue>`), lists the AC checklist, the migration, the removed `GET /api/associations/{id}/features` endpoint, the new API-key scopes (`managed:write`, `managed:read`, `quota:read`, `voting:write`, `members:write`) and the rollback note from the spec (wait for drafts to settle, delete failed proposals, close password-protected ones before reverting).
3. Release: none — no version bump, tag or release.
4. **Owner OK required before:** pushing `feature/saas-demo`; opening the PR; merging. Before merge the owner runs `./e2e.sh` (needs `.env`) against a saas-backend with #760, #819, #844, #855 and the run is recorded in the PR (AC11). If it cannot be run, the PR says so and lists the unverified upstream assumptions: `managedBy` on the managed-orgs list, wallet read with an API key, `0x` address in the wallet path, password hash accepted at step 0.
5. Post-merge checks: `docker compose up --build` — the API log shows the `PublishStateAndPassword` migration applied on startup; admin page shows the integrator panel (or "Integrator status unavailable" with a narrow key); creating a ≤ 25-voter proposal answers 201/202 and reaches `Open`.
