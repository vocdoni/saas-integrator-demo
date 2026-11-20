# saas-backend sync (Sep → Nov 2026): design

Status: audited   Class: architectural
Owner decisions so far (2026-11-20): fix everything broken + show pricing/wallet, extend end date,
member password credential; deliver as one PR on `feature/saas-demo` with a linked issue.

## Problem

The demo was last synced with saas-backend at #652 (2026-09-02, upstream `99a01c3`). Since then
~60 upstream commits landed. Diffing the swagger at `99a01c3` against `main` (2026-11-20) shows:

1. **Broken today.** `GET /organizations/{org}/subscription` and `GET /plans` were removed (#855).
   `VocdoniClient.GetSubscriptionFeaturesAsync` (`Services/Vocdoni/VocdoniClient.cs:136-166`) calls
   both, so `GET /api/associations/{id}/features` (`AssociationsController.cs:110`) answers 502 and
   the proposal form (`web/src/components/Proposals.jsx:54`) cannot read the anonymous flag.
   Anonymous voting is now on every plan (#718); the only gate left is a service-wide flag (#841)
   that makes `POST /processes` answer 400 (40037) "anonymous voting is temporarily unavailable".
2. **Pay-per-process (#819).** Publishing debits the payer's wallet; for a managed org (every
   association) that is the integrator's wallet. Free up to 25 voters, priced above. Publish can
   answer 402 (40175 integrator wallet short, 40183 spend cap), 422 (40176 > 50 000 voters), 409, 503.
   The publish job can stay pending from minutes to hours while it retries transient chain failures.
   `ProposalsController.Create` (`ProposalsController.cs:24-93`; create/publish/save at 59-90)
   creates the draft, publishes, polls ~40 s (`VocdoniClient.PollJobAsync`), and only then saves the
   local row. Any publish failure or poll timeout leaves an **orphan upstream draft** (now capped at
   20 per org) and, on timeout, a process that may still publish **and be charged** with no local
   record. A publish that failed after submitting elections cannot be deleted upstream (409
   40903/40904); its documented way out is to publish again, which adopts at no new charge.
3. **Managed-org creation is capped** (#819): until the integrator's wallet holds a top-up it may
   create only one managed org; the next answers 402 (40184). Today that surfaces as a raw 502, and
   `e2e.sh:76` still greps the retired code 40154.
4. **Upstream errors are unreadable**: `VocdoniApiException` (`VocdoniClient.cs:8`) carries the raw
   body; the 502 handler (`Program.cs:61-74`) passes it through as `detail`.
5. **SDK drift**: `@vocdoni/api-client` 2.0.0 → 2.8.0, `@vocdoni/api-voting` 1.1.0 → 1.3.3.
   `client.processes` is a deprecated alias of `client.elections` (api-client 2.1.0); `jobs.waitFor`
   now waits 5 min with retries (2.8.0); `hashMemberPassword(orgAddress, password)` exists (2.7.0).
6. **Dead script**: `create-process.sh` calls `/census` and `/process`, removed in #725.
7. **Limits changed**: max 15 questions per process, 90-day processes, 20 drafts (#819).

New upstream features the owner wants shown:
- **Pricing + wallet** (#819): `GET /pricing?voters=N` (public calculator, voters ≥ 1),
  `GET /integrator` (`canCreateManagedOrg`, `managedOrgsCreated`, `netTopUpCents`),
  `GET /organizations/{integratorOrg}/wallet` (`balanceCents`, `currency`).
- **Extend end date** (PR #844, issue #843): `PUT /processes/{id}/endDate {endDate}` → 202 job.
- **Member password** (#760): census auth field `password`; members carry `password` as a 64-hex
  client hash (`hashMemberPassword`), reads report `hasPassword`; voter step 0 sends
  `AuthRequest.password` (same hash). A wrong password answers like an unknown member (40802).

## Goals
1. No call to a removed endpoint remains; the anonymous toggle always works and an
   "anonymous unavailable" refusal surfaces as a readable error.
2. A proposal create never leaves an untracked upstream draft or an untracked published process,
   and every proposal stuck in publishing can be resolved from the demo (retry or delete).
3. Upstream billing/limit refusals reach the UI as a readable sentence.
4. The admin sees the integrator's wallet balance and whether another association can be created;
   the owner sees the estimated price of a proposal before creating it.
5. The owner can extend the end date of an open proposal.
6. The owner can set a homeowner's password and require it on a proposal; voters then enter it.
   The plaintext password never leaves the browser.
7. SDK on current versions; docs (README, CLAUDE.md, requests.http, .env.example) and `e2e.sh` match
   the new behaviour.

## Non-goals
- Stripe checkout / wallet top-up from the demo (the integrator tops up in the Vocdoni portal).
- Per-association spend caps (`PUT /integrator/organizations/{org}`).
- Paused publish (#668), `endedAt` display (#729), certificate auth (#862), on-chain `hasVoted`
  (#795), census headroom checkout.
- Clearing a homeowner's password, bulk password import.
- Changing the 502 mapping of upstream failures (CLAUDE.md: keep it).

## Current state
- Proposal create: `ProposalsController.cs:24-93`. Census is `{authFields:["memberNumber"],
  memberIds, anonymous?}`. Local row saved only after publish + read-back.
- `ProposalStatus { Draft, Open, Closed }` (`Models/Proposal.cs:3`), stored as int; `Draft` unused.
- Read reconcile: `HydrateAsync` (`ProposalsController.cs:218-250`) reads `GET /processes/{id}` per
  proposal, refreshes question status, marks Closed when all questions ended or `EndDate` passed.
- `Close` (`ProposalsController.cs:131-135`) forwards to upstream for any status.
- Public page: `VotingController.Get` (`VotingController.cs:21-57`) serves stored values, never
  saves; `VotingPage.jsx:114` drops questions without `upstreamId`.
- Job polling: `VocdoniClient.PollJobAsync` (20 × 2 s, throws on `failed`/timeout), also used by
  `SetQuestionCensusAsync`.
- Homeowners: `HomeownersController.cs` list/add/delete via `/organizations/{org}/members`.
- Voting page: `web/src/voting.js:castProcessVotes` → `client.processes.authStep0(processId,
  {memberNumber})`, `signBatch` / blind sign, `client.elections.voteBatch`, `client.jobs.waitFor`.
- Owner list: `Proposals.jsx:277-278` derives the badge via `isFinished(p)` (end date passed →
  Closed); Close shown when not finished (`:288`); voting link whenever `vocdoniProcessId` set (`:320`).
- The web image proxies `/api` through nginx (no `proxy_read_timeout` → 60 s), so a request that
  polls for minutes is not an option.
- Tests: three unit-test classes (VocdoniClient, AssociationAccess, ToQuestionRequest); no controller
  or HTTP-level tests, no `WebApplicationFactory` (CLAUDE.md's claim is stale).

## Design

### A. Remove plan gating
Delete `GetSubscriptionFeaturesAsync`, `FindManagedOrgPlanIdAsync`, the subscription/plan wire
models (`OrganizationSubscriptionInfo`, `SubscriptionPlan`, `SubscriptionFeatures`,
`ManagedOrgSubscription`, `VocdoniPlan`), `AssociationsController.Features`,
`AssociationFeaturesResponse`, their tests, and the UI fetch/disable in `Proposals.jsx`. The
anonymous toggle is always enabled. Remove the unused `Vocdoni__IntegratorAddress` line from
`docker-compose.yml`.

### B. Readable upstream errors
`VocdoniApiException` gains `int? Code` and `JsonElement? Data`, parsed best-effort from the
upstream `errors.Error` body (`{error, code, data}`). The mapping moves out of the `Program.cs`
lambda into `internal static (int? Code, string Detail) UpstreamErrors.Describe(Exception ex)`; the
lambda keeps status 502 and writes `{error: "vocdoni_upstream", code, detail}`. `detail` is a
readable sentence for the codes below, else the upstream `error` text, else the raw message:

| Code | Sentence (amounts from `data`, cents → €) |
|---|---|
| 40175 | The integrator's wallet doesn't cover this (needs €X, available €Y). Top it up in the Vocdoni portal. — the parenthesis is dropped when `data` lacks `requiredCents` or `availableCents`. |
| 40183 | This association reached the spending cap set by its integrator. |
| 40184 | Top up the integrator wallet in the Vocdoni portal to create another association. |
| 40176 | Over 50 000 voters: this proposal needs a custom quote from Vocdoni. |

The SPA already shows `detail`.

### C. Resilient proposal create

`VocdoniClient` changes: `PublishVotingProcessAsync(processId)` returns the job id (or null when
upstream answered 200 already-published) without polling. New `WaitForJobAsync(jobId)` returns
`(JobState State, string? Error)` with `State` ∈ `Completed | Failed | Pending` after the existing
bounded poll; new non-polling `GetJobAsync(jobId)` for the reconcile read. `PollJobAsync` stays a
private wrapper that throws on `Failed` and `Pending`, so `SetQuestionCensusAsync` is unchanged.
New `DeleteVotingProcessAsync(processId)`.

Create:
1. Validate as today, plus: at most 15 questions; `EndDate - StartDate` ≤ 90 days.
2. `POST /processes` → `processId`, `missingData`.
3. Save the local `Proposal` with `Status = Draft`, `VocdoniProcessId`, `PublishJobId = null`,
   questions without `UpstreamId`.
4. `POST /processes/{id}/publish` (the shared **publish routine**, steps 4–6):
   - non-2xx → `DELETE /processes/{id}`. If the delete succeeds or answers 404, delete the local
     row. If it fails any other way, keep the row: `Status = Failed`, `PublishError` = the publish
     error text. Either way rethrow the publish error (→ 502 with the readable detail).
   - no answer (`HttpRequestException`, timeout, cancellation) → outcome unknown: keep the row
     `Draft` with `PublishJobId = null` and rethrow. Reconciliation settles it.
   - 200 (already published) → 6.
   - 202 → store `PublishJobId`, save.
5. `WaitForJobAsync`:
   - `Completed` → 6.
   - `Failed` → never auto-delete (upstream may have submitted elections; its delete answers 409
     40903/40904). `Status = Failed`, `PublishError` = the job errors joined with "; ", save, 502
     with that text.
   - `Pending` → answer **202** with the proposal (status `Draft`).
6. Read back `GET /processes/{id}`; fill each question's `UpstreamId`/`Status` by order, `ChainId`;
   `Status = Open`; save; answer 201.

Every `SaveChanges` that follows an upstream side effect (steps 3–6 and the cleanup deletes) uses
`CancellationToken.None`, so a client disconnect cannot drop the record of what upstream did.

The create response (201 or 202) carries `MissingData` (upstream `missingData`, not persisted);
the owner UI shows "N homeowners were left out of this proposal (missing data, e.g. no password).
Setting it later does not add them." when non-empty.

**Reconcile** — one method shared by `ProposalsController.HydrateAsync` and `VotingController.Get`;
both save what changed. Per proposal, in this order:
1. For every `Draft` (with or without `PublishJobId`): if the process read succeeded, says
   `published: true`, and carries a non-empty `upstreamId` for every local question (matched by
   order), fill as in step 6 and set `Open`. Otherwise, if `PublishJobId` is set, `GetJobAsync`:
   `failed` → `Status = Failed`, `PublishError` = errors joined with "; ". `pending`, `completed`
   without a published read yet, 404, or any read error → stay `Draft`. A `Draft` without job id
   that is not published stays `Draft`.
2. `EndDate` sync (E): copy the process read's `endDate` when it parses and differs.
3. Question status refresh (as today).
4. Closed rule (every question ended, or `EndDate` passed) applies only to `Open` proposals;
   `Draft` and `Failed` are never closed by date.
The reconcile only promotes or fails rows; it never deletes. Its save catches
`DbUpdateConcurrencyException` (row deleted concurrently) and serves what it read.

The public endpoint (`VotingController.Get`) answers 404 for a proposal still `Draft` or `Failed`
after reconciling.

New endpoints (owner/admin via `AssociationAccess`):
- `DELETE api/associations/{a}/proposals/{id}` — only `Draft` or `Failed` (409 otherwise):
  upstream `DELETE /processes/{id}`; 200 or 404 → delete the local row, 204; any other upstream
  failure → 502 and the row stays.
- `POST api/associations/{a}/proposals/{id}/publish` — only `Failed` (409 otherwise): clear
  `PublishError`, set `Draft`, run the publish routine, except that a non-2xx publish puts the row
  back to `Failed` with the new error instead of deleting anything. Upstream adopts any election
  that already mined, at no new charge.
- `POST .../close` answers 409 for a proposal that is not `Open`.

Owner UI states. `Draft`: badge "Publishing…", no Close, no voting link, no eligibility editor, a
Delete button behind a confirm ("only if publishing is stuck"); while any listed proposal is
`Draft` the list refetches every 5 s (same pattern as the existing close poll). `Failed`: badge
"Failed", the stored `PublishError`, Retry publish and Delete. `isFinished` is consulted only for
`Open` and `Closed`. After create the form reads the returned `status` (the fetch wrapper does not
expose 201 vs 202). The voting link shows only for `Open` and `Closed`.

### D. Pricing and wallet
- `IVocdoniClient.GetPriceAsync(int voters)` → `GET /pricing?voters=N` → `ProcessPrice
  {totalCents, currency, lines[{kind, description, amountCents}], quoteRecommended, quoteRequired}`.
- `GET api/associations/{id}/price?requirePassword=bool` (owner/admin): voters = homeowner count
  (only those with `hasPassword` when `requirePassword`). With 0 voters it answers `{voters: 0,
  totalCents: 0, currency: "eur", quoteRecommended: false, quoteRequired: false}` without calling
  upstream (`/pricing` needs voters ≥ 1, 400 code 40010). Otherwise `{voters, totalCents, currency,
  quoteRecommended, quoteRequired}` from `/pricing`. The form shows, in this order: "No eligible
  homeowners" when voters is 0; the quote notice when `quoteRequired`; "Free" when `totalCents` is
  0; else "Estimated cost: €X for N voters, paid from the integrator wallet". Refetched when the
  password toggle changes. `currency` arrives lowercase (`eur`).
- `IVocdoniClient.GetIntegratorInfoAsync()` → `GET /integrator`; `GetWalletAsync(orgAddress)` →
  `GET /organizations/{org}/wallet?page=1&limit=1` (balance only).
- The integrator org address is the `managedBy` of the first managed org in
  `GET /integrator/organizations?page=1&limit=1`, read on each `GET api/integrator` call (no cache:
  one small request on an admin-only page).
- `GET api/integrator` (admin only) → `{canCreateManagedOrg, managedOrgsCreated, balanceCents?,
  currency?}`; `balanceCents` is null when no managed org exists or the wallet read fails (e.g. the
  key lacks `quota:read`). If `GET /integrator` itself fails, the endpoint answers 502 as usual and
  AdminPage shows "Integrator status unavailable" without blocking the associations list. When
  `canCreateManagedOrg` is false, AdminPage shows that a top-up is needed before creating another
  association.

### E. Extend end date
- `IVocdoniClient.SetProcessEndDateAsync(processId, DateTimeOffset)` → `PUT
  /processes/{id}/endDate {endDate: RFC 3339 UTC}` → job id.
- `PUT api/associations/{a}/proposals/{id}/end-date {endDate}` (owner/admin), only for `Open`
  proposals whose new end is later than the current one (400 otherwise; upstream enforces the
  5-minute / 90-day rules and its 400 surfaces via B). `WaitForJobAsync`: `Completed` → save the
  new `EndDate`, 200 with the proposal; `Failed` → 502 with the job error, `EndDate` unchanged;
  `Pending` → 202, `EndDate` unchanged (the reconcile copies upstream `endDate` later).
- Owner UI: on an open proposal, a datetime input prefilled with the current end + "Extend" button.

### F. Member password
- Hashing happens **in the browser** with `hashMemberPassword(orgAddress, password)` from
  `@vocdoni/api-client`; the demo backend only receives and forwards the 64-hex hash and never
  stores or logs it. The backend validates `^[0-9a-f]{64}$` (400 otherwise).
- `AddHomeownerRequest` gains `string? PasswordHash` → `VocdoniOrgMember.Password`.
- `PUT api/associations/{a}/homeowners/{memberId}/password {passwordHash}` (owner/admin). The
  controller first checks `memberId` is the id of a member returned by `ListMembersAsync(org)` and
  answers 404 otherwise (upstream treats an unused id as a create, which would add a phantom member
  and can grow and charge a live census). Then upstream `PUT /organizations/{org}/members {id,
  password}` (omitted fields keep their values).
- `HomeownerResponse` gains `bool HasPassword` (from upstream `hasPassword`).
- `CreateProposalRequest.RequirePassword` → census `authFields: ["memberNumber","password"]`;
  persisted as `Proposal.RequirePassword`; exposed on `ProposalResponse` and on
  `VotingInfoResponse` together with the association's `OrgAddress` (needed for the hash).
- Memberbase UI: optional password field on add; "Set password" action per homeowner; a
  "has password" marker.
- Voting page: when `requirePassword`, a password input; `castProcessVotes` receives the plaintext
  and `orgAddress`, hashes with `hashMemberPassword`, sends `{memberNumber, password}` to step 0.
- Password inputs require a non-blank value (`hashMemberPassword` throws on empty). Hashing needs a
  secure context (HTTPS or localhost); the pages show the SDK's error otherwise. On a failed step 0
  the voting page shows "Check your member number and password" for code 40802 (or the existing
  member-number text when no password is required) and "Too many attempts, try again later" for
  40017.

### G. SDK, scripts, docs
- `@vocdoni/api-client` ^2.8.0, `@vocdoni/api-voting` ^1.3.3; `client.processes.*` →
  `client.elections.*`; update `voting.test.js` mocks.
- Delete `create-process.sh`. Fix the "100 questions" comment in `voting.js` to 15.
- `e2e.sh`: detect the managed-org cap by 402 / code 40184 (was 40154); accept 201 or 202 on
  proposal create and, on 202, poll `GET .../proposals/{id}` until `status` is `Open` (fail on
  `Failed`); add a step that reads `GET /api/integrator` and prints the balance.
- README and `.env.example`: the API key needs `managed:write`, `managed:read`, `quota:read`,
  `voting:write` and `members:write`.
- README, CLAUDE.md (drop plan gating, describe billing/password/end-date/publish states, correct
  test description and counts), `requests.http` (drop `/features`, add price/integrator/end-date/
  password/delete/retry examples).

### Alternatives considered
| Option | Why not |
|---|---|
| Poll publish synchronously up to 5 min | nginx's 60 s proxy timeout cuts the request; a timeout would still lose the local record of a charged process. |
| Hash passwords in the .NET backend | The plaintext would cross our backend; upstream's contract is that it never leaves the browser, and the SDK ships the hash. |
| Integrator address from new config | `managedBy` gives it for free once one association exists; with none there is nothing to pay for. |
| Price the real draft (`GET /processes/{id}/price`) | Needs a draft before the owner decides; the public calculator gives the same number for our add-on-free processes. |
| Keep `/features` returning `anonymousVoting: true` | Dead endpoint kept for nothing; the flag is no longer per-org. |
| Auto-delete on a failed publish job | Upstream refuses (409 40903/40904) while elections may mine; deleting locally would orphan a paid process. |

## Compatibility and rollout
- One EF migration: `Proposal.RequirePassword` (bool, default false), `Proposal.PublishJobId`
  (string?, null), `Proposal.PublishError` (string?, null); `ProposalStatus.Failed` appended (int 3).
  Existing rows keep their meaning. Applied on startup as today.
- Removed demo endpoint: `GET /api/associations/{id}/features` (only the bundled SPA used it).
- Requires the deployed saas-backend to include #760, #819, #844, #855 (all on `main`).
- API key scopes: `managed:write`, `managed:read`, `quota:read`, `voting:write`, `members:write`.
- Rollback: revert the PR. The columns are additive and ignored by old code. Proposals created
  under the new version are not all usable afterwards: a `Draft` row is never promoted, a `Failed`
  row shows status `3`, and voters cannot authenticate on a `RequirePassword` proposal. Before
  reverting, wait for drafts to settle, delete failed proposals, and close password-protected ones.

## Failure modes and security
- **Plaintext password**: only in the browser; API rejects anything but a 64-hex hash; no logging
  of request bodies containing it. On the public voting page the hash goes straight to the Vocdoni
  API, never to the demo backend.
- **Phantom members**: password PUT checks the member exists first (F).
- **Orphan drafts / lost charges**: covered by C. A failed upstream delete keeps the local row
  (`Failed`), and Retry publish resolves drafts upstream won't delete.
- **Concurrent reads during publish**: the reconcile promotes `Draft → Open` only from a process
  read that says `published` with every `upstreamId` present; it never deletes.
- **Row deleted during a read**: the reconcile save catches `DbUpdateConcurrencyException` and
  serves what it read.
- **Wallet read with a narrow key**: 403 → balance null, no 502.
- **Upstream error body not JSON**: `Code`/`Data` null; raw message as today.
- **Voter brute force**: upstream budgets step 0 (40017); the page shows the throttle message.

## Test approach
No controller or HTTP-level test exists today and none of the tooling for one. This PR adds, with
no new package:
- `FakeVocdoniClient : IVocdoniClient` in the test project (scripted returns/throws per method,
  records calls).
- Controller tests construct controllers directly with an `AppDbContext` on SQLite
  `Data Source=:memory:` (one open connection per test, `EnsureCreated`) and a `ControllerContext`
  whose `User` carries the `sub` and role claims `ApiControllerBase` reads.
- AC3 tests `UpstreamErrors.Describe` directly.
- The fake answers `WaitForJobAsync` directly, so no test waits on the real poll delay.

## Acceptance criteria
- [ ] AC1: `rg -n 'GetSubscriptionFeatures|/subscription|/plans|associations/[^ ]*/features|AssociationFeaturesResponse|create-process' src tests web/src README.md CLAUDE.md requests.http e2e.sh .env.example docker-compose.yml`
      prints nothing, and `create-process.sh` does not exist.
- [ ] AC2: xUnit: create with a publish 402 deletes the upstream draft and leaves no local row; the
      same 402 with a failing upstream delete (409) leaves a `Failed` row carrying the error; a
      publish job that fails inside the request leaves a `Failed` row and answers 502; a pending job
      answers 202 with a `Draft` proposal; a later read whose process is published promotes it to
      `Open` with upstream ids (also via `VotingController.Get`, which answers 404 for `Draft`/
      `Failed`); a later read with a failed job marks it `Failed`; a `Failed` proposal past its end
      date stays `Failed`; DELETE answers 409 for `Open`, removes the row on upstream 200/404, and
      answers 502 keeping the row on upstream 409; retrying a `Failed` proposal whose job completes
      makes it `Open`; close answers 409 for a non-`Open` proposal.
- [ ] AC3: xUnit: `UpstreamErrors.Describe` returns `code` and the readable sentence for 40175
      (with amounts, and without when `data` lacks them) and 40184, the upstream `error` text for a
      40037 "anonymous voting is temporarily unavailable" body, and the raw message for a non-JSON
      body.
- [ ] AC4: xUnit: `GET api/associations/{id}/price` counts only password holders when
      `requirePassword=true` and forwards `voters` to `/pricing`; with zero matching homeowners it
      answers `voters: 0` and makes no `/pricing` call.
- [ ] AC5: xUnit: `GET api/integrator` returns the balance from the wallet of the discovered
      `managedBy`, and null balance when there is no managed org or the wallet read 403s.
- [ ] AC6: xUnit: end-date extension sends RFC 3339 UTC, saves the new end on a completed job, 400s
      for a non-later date or a non-`Open` proposal; a pending job answers 202 and leaves `EndDate`
      unchanged; a failed job answers 502 and leaves it unchanged; a later read copies the upstream
      `endDate`.
- [ ] AC7: xUnit: a password hash that isn't 64 lowercase hex is a 400; a valid one reaches the
      upstream member `password`; a password PUT for an id that is not a current member is a 404
      and sends no upstream write; `RequirePassword` sends `authFields ["memberNumber","password"]`.
- [ ] AC8: vitest: `castProcessVotes` with a password hashes it with the org address and passes
      `password` to `elections.authStep0`; without one, no `password` key is sent; all existing
      voting tests pass against `client.elections`.
- [ ] AC9: `dotnet test`, `npm test`, `npm run build` all green.
- [ ] AC10: README/CLAUDE.md/requests.http/.env.example describe billing, scopes, password,
      end-date and publish states, and no longer describe plan gating.
- [ ] AC11: `./e2e.sh` passes against a backend that includes #760, #819, #844 and #855, run by the
      owner before merge (needs `.env` credentials; record the run in the PR). If it cannot be run,
      the PR description says so and lists the unverified upstream assumptions: `managedBy` on the
      managed-orgs list, wallet read with an API key, `0x` address in the wallet path, password
      hash accepted at step 0.

## Constraints
- New Vocdoni calls go in `IVocdoniClient` + `VocdoniClient`, never inline in a controller.
- Upstream failures stay 502 (`VocdoniApiException` → `Program.cs`), no retries.
- Controllers stay thin; ownership via `AssociationAccess`.
- Hex addresses on the wire. `ponytail:` comments mark deliberate shortcuts.
- No local .NET SDK: build/test/migrate in `mcr.microsoft.com/dotnet/sdk:10.0` as CLAUDE.md shows.
- Commits: conventional `type(scope): summary`, never signed (`git -c commit.gpgsign=false`), no
  AI attribution or Co-authored-by trailers, explicit paths, no push.

## Open questions
(none)
