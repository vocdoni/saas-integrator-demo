# saas-backend sync (Sep → Nov 2026): design

Status: draft   Class: architectural
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
   The publish job can also stay pending for minutes while it retries transient chain failures.
   `ProposalsController.Create` (`ProposalsController.cs:57-90`) creates the draft, publishes, polls
   ~40 s (`VocdoniClient.PollJobAsync`), and only then saves the local row. Any publish failure or
   poll timeout leaves an **orphan upstream draft** (now capped at 20 per org) and, on timeout, a
   process that may still publish **and be charged** with no local record.
3. **Managed-org creation is capped** (#819): until the integrator's wallet holds a top-up it may
   create only one managed org; the next answers 402 (40184). Today that surfaces as a raw 502.
4. **Upstream errors are unreadable**: `VocdoniApiException` (`VocdoniClient.cs:9`) carries the raw
   body; the 502 handler (`Program.cs:60-73`) passes it through as `detail`.
5. **SDK drift**: `@vocdoni/api-client` 2.0.0 → 2.8.0, `@vocdoni/api-voting` 1.1.0 → 1.3.3.
   `client.processes` is a deprecated alias of `client.elections` (1.2.0); `jobs.waitFor` now waits
   5 min with retries (2.8.0); `hashMemberPassword(orgAddress, password)` exists (2.7.0).
6. **Dead script**: `create-process.sh` calls `/census` and `/process`, removed in #725.
7. **Limits changed**: max 15 questions per process, 90-day processes, 20 drafts (#819).

New upstream features the owner wants shown:
- **Pricing + wallet** (#819): `GET /pricing?voters=N` (public calculator), `GET /integrator`
  (`canCreateManagedOrg`, `managedOrgsCreated`, `netTopUpCents`),
  `GET /organizations/{integratorOrg}/wallet` (`balanceCents`, `currency`).
- **Extend end date** (#843): `PUT /processes/{id}/endDate {endDate}` → 202 job.
- **Member password** (#760): census auth field `password`; members carry `password` as a 64-hex
  client hash (`hashMemberPassword`), reads report `hasPassword`; voter step 0 sends
  `AuthRequest.password` (same hash). A wrong password answers like an unknown member (40802).

## Goals
1. No call to a removed endpoint remains; the anonymous toggle always works and an
   "anonymous unavailable" refusal surfaces as a readable error.
2. A proposal create never leaves an untracked upstream draft or an untracked published process.
3. Upstream billing/limit refusals reach the UI as a readable sentence.
4. The admin sees the integrator's wallet balance and whether another association can be created;
   the owner sees the estimated price of a proposal before creating it.
5. The owner can extend the end date of an open proposal.
6. The owner can set a homeowner's password and require it on a proposal; voters then enter it.
   The plaintext password never leaves the browser.
7. SDK on current versions; docs (README, CLAUDE.md, requests.http) match the new behaviour.

## Non-goals
- Stripe checkout / wallet top-up from the demo (the integrator tops up in the Vocdoni portal).
- Per-association spend caps (`PUT /integrator/organizations/{org}`).
- Paused publish (#668), `endedAt` display (#729), certificate auth (#862), on-chain `hasVoted`
  (#795), census headroom checkout.
- Clearing a homeowner's password, bulk password import.
- Changing the 502 mapping of upstream failures (CLAUDE.md: keep it).

## Current state
- Proposal create: `ProposalsController.cs:25-90`. Census is `{authFields:["memberNumber"],
  memberIds, anonymous?}`. Local row saved only after publish + read-back.
- `ProposalStatus { Draft, Open, Closed }` (`Models/Proposal.cs:3`); `Draft` is unused today.
- Read reconcile: `HydrateAsync` (`ProposalsController.cs:220-252`) reads `GET /processes/{id}` per
  proposal, refreshes question status, marks Closed.
- Job polling: `VocdoniClient.PollJobAsync` (20 × 2 s, throws on `failed`/timeout).
- Homeowners: `HomeownersController.cs` list/add/delete via `/organizations/{org}/members`.
- Voting page: `web/src/voting.js:castProcessVotes` → `client.processes.authStep0(processId,
  {memberNumber})`, `signBatch` / blind sign, `client.elections.voteBatch`, `client.jobs.waitFor`.
  `VotingInfoResponse` (`Dtos.cs`) carries `ApiUrl`, `ChainId`, `Anonymous`.
- The web image proxies `/api` through nginx (default 60 s read timeout), so a request that polls
  for minutes is not an option.

## Design

### A. Remove plan gating
Delete `GetSubscriptionFeaturesAsync`, `FindManagedOrgPlanIdAsync`, the subscription/plan wire
models (`OrganizationSubscriptionInfo`, `SubscriptionPlan`, `SubscriptionFeatures`,
`ManagedOrgSubscription`, `VocdoniPlan`), `AssociationsController.Features`,
`AssociationFeaturesResponse`, their tests, and the UI fetch/disable in `Proposals.jsx`. The
anonymous toggle is always enabled.

### B. Readable upstream errors
`VocdoniApiException` gains `int? Code` and `JsonElement? Data`, parsed best-effort from the
upstream `errors.Error` body (`{error, code, data}`). The 502 handler keeps status 502 and adds
`code` to the JSON; `detail` becomes a readable sentence for the codes below, else the upstream
`error` text (falling back to the current raw message):

| Code | Sentence (amounts from `data`, cents → €) |
|---|---|
| 40175 | The integrator's wallet doesn't cover this proposal (needs €X, available €Y). Top it up in the Vocdoni portal. |
| 40183 | This association reached the spending cap set by its integrator. |
| 40184 | Top up the integrator wallet in the Vocdoni portal to create another association. |
| 40176 | Over 50 000 voters: this proposal needs a custom quote from Vocdoni. |

The SPA already shows `detail`; no UI change beyond that.

### C. Resilient proposal create
1. Validate as today, plus: at most 15 questions; `EndDate - StartDate` ≤ 90 days.
2. `POST /processes` → `processId`, `missingData`.
3. Save the local `Proposal` with `Status = Draft`, `VocdoniProcessId`, `PublishJobId = null`,
   questions without `UpstreamId`.
4. `POST /processes/{id}/publish`:
   - non-2xx → best-effort `DELETE /processes/{id}` (ignore its failure), delete the local row,
     rethrow (→ 502 with the readable detail).
   - 200 (already published) → go to 6.
   - 202 → store `PublishJobId`, save.
5. Poll the job with the existing bounded poll (~40 s, fits nginx):
   - `completed` → 6.
   - `failed` → best-effort `DELETE /processes/{id}`, delete the local row, rethrow.
   - timeout → return **202** with the proposal (status `Draft`) — the owner UI shows "Publishing…".
6. Read back `GET /processes/{id}`; fill each question's `UpstreamId`/`Status` by order, `ChainId`;
   `Status = Open`; save; return 201.

`HydrateAsync` additionally, for a `Draft` proposal with a `PublishJobId`: read `GET /jobs/{id}`;
on `completed` run step 6's fill from the process read it already does; on `failed` set
`Status = Failed` (new enum value, appended so stored ints don't shift) and keep the job's error
text in a new `PublishError` column. A pending job leaves it `Draft`.

New `DELETE api/associations/{a}/proposals/{id}` — only for `Draft` or `Failed` proposals (409
otherwise): best-effort upstream `DELETE /processes/{id}` (a 404 counts as gone; any other upstream
failure → 502 and the local row stays), then delete the local row. The owner UI shows a Delete
button on `Failed` proposals with the stored error.

The create response (201 or 202) carries `MissingData` (the upstream `missingData` list, member
identifiers left out of the census, e.g. homeowners without a password); the owner UI shows
"N homeowners were left out (missing data)" when non-empty. Not persisted.

`VocdoniClient.PublishVotingProcessAsync` is split so the controller can see the job id: it returns
the job id (or null when upstream said 200 already published) without polling; polling becomes a
separate `WaitForJobAsync(jobId)` returning `completed | failed(error) | pending`.

### D. Pricing and wallet
- `IVocdoniClient.GetPriceAsync(int voters)` → `GET /pricing?voters=N` → `ProcessPrice
  {totalCents, currency, lines[{kind, description, amountCents}], quoteRecommended, quoteRequired}`.
- `GET api/associations/{id}/price?requirePassword=bool` (owner/admin): voters = homeowner count
  (only those with `hasPassword` when `requirePassword`); returns `{voters, totalCents, currency,
  quoteRecommended, quoteRequired}`. The proposal form shows "Estimated cost: €X for N voters, paid
  from the integrator wallet" (or "Free" when 0, or the quote notice), refetched when the password
  toggle changes.
- `IVocdoniClient.GetIntegratorInfoAsync()` → `GET /integrator`; `GetWalletAsync(orgAddress)` →
  `GET /organizations/{org}/wallet?page=1&limit=1` (balance only).
- The integrator org address is discovered as the `managedBy` of the first managed org in
  `GET /integrator/organizations?page=1&limit=1`, cached for the process lifetime once found.
- `GET api/integrator` (admin only) → `{canCreateManagedOrg, managedOrgsCreated, balanceCents?,
  currency?}`; `balanceCents` is null when no managed org exists yet or the wallet read fails
  (e.g. the key lacks `quota:read`). AdminPage shows the balance (or "unavailable") and, when
  `canCreateManagedOrg` is false, a notice that a top-up is needed before creating another
  association.

### E. Extend end date
- `IVocdoniClient.SetProcessEndDateAsync(processId, DateTimeOffset)` → `PUT
  /processes/{id}/endDate {endDate: RFC 3339 UTC}` → job id.
- `PUT api/associations/{a}/proposals/{id}/end-date {endDate}` (owner/admin), only for `Open`
  proposals whose new end is later than the current one (400 otherwise; upstream enforces the
  5-minute / 90-day rules and its 400 surfaces via B). Poll the job: `completed` → save the new
  `EndDate`, 200 with the proposal; `failed` → 502 with the job error; timeout → 202.
- `HydrateAsync` syncs `EndDate` from the process read's `endDate` when it parses and differs, so a
  202 converges on the next read.
- Owner UI: on an open proposal, a datetime input prefilled with the current end + "Extend" button.

### F. Member password
- Hashing happens **in the browser** with `hashMemberPassword(orgAddress, password)` from
  `@vocdoni/api-client`; the demo backend only ever receives and forwards the 64-hex hash and never
  stores or logs it. The backend validates `^[0-9a-f]{64}$` (400 otherwise).
- `AddHomeownerRequest` gains `string? PasswordHash` → `VocdoniOrgMember.Password`.
- `PUT api/associations/{a}/homeowners/{memberId}/password {passwordHash}` → upstream
  `PUT /organizations/{org}/members {id, password}` (upsert; omitted fields keep their values).
- `HomeownerResponse` gains `bool HasPassword` (from upstream `hasPassword`).
- `CreateProposalRequest.RequirePassword` → census `authFields: ["memberNumber","password"]`;
  persisted as `Proposal.RequirePassword`; exposed on `ProposalResponse` and on
  `VotingInfoResponse` together with the association's `OrgAddress` (needed for the hash).
- Memberbase UI: optional password field on add; a "Set password" action per homeowner; a
  "has password" marker.
- Voting page: when `requirePassword`, a password input; `castProcessVotes` receives the plaintext
  and `orgAddress`, hashes with `hashMemberPassword`, and sends `{memberNumber, password}` to step 0.
  The error text for a failed step 0 mentions the password when one is required.

### G. SDK + cleanup
- `@vocdoni/api-client` ^2.8.0, `@vocdoni/api-voting` ^1.3.3; `client.processes.*` →
  `client.elections.*`; update `voting.test.js` mocks.
- Delete `create-process.sh`. Fix the "100 questions" comment in `voting.js` to 15.
- README, CLAUDE.md (drop the plan-gating bullet, describe billing/password/end-date, test counts),
  `requests.http` (drop `/features`, add price/end-date/password examples).

### Alternatives considered
| Option | Why not |
|---|---|
| Poll publish synchronously up to 5 min | nginx's 60 s proxy timeout cuts the request; a timeout would still lose the local record of a charged process. |
| Hash passwords in the .NET backend | The plaintext would cross our backend; upstream's contract is that it never leaves the browser, and the SDK already ships the hash. |
| Read the integrator address from new config | One more secret to set; `managedBy` gives it for free once one association exists, and with none there is nothing to pay for yet. |
| Price the real draft (`GET /processes/{id}/price`) | Needs a draft before the owner decides; the public calculator gives the same number for our add-on-free processes. |
| Keep `/features` returning `anonymousVoting: true` | Dead endpoint kept for nothing; the flag is no longer per-org. |

## Compatibility and rollout
- One EF migration: `Proposal.RequirePassword` (bool, default false), `Proposal.PublishJobId`
  (string?, null), `Proposal.PublishError` (string?, null); `ProposalStatus.Failed` appended (int 3).
  Existing rows keep their meaning. Applied on startup as today.
- Removed demo endpoint: `GET /api/associations/{id}/features` (only the bundled SPA used it).
- Requires the deployed saas-backend to include #760, #819, #843, #855 (all on `main`). A backend
  older than #855 would still work except that the removed `/features` gate is gone.
- Rollback: revert the PR; the migration's columns are additive and ignored by the old code.

## Failure modes and security
- **Plaintext password**: only in the browser; API rejects anything but a 64-hex hash; no logging
  of request bodies containing it. The voting page is public: the hash is computed client-side and
  sent straight to the Vocdoni API, not to the demo backend.
- **Orphan drafts / lost charges**: covered by C. Best-effort deletes may fail (e.g. 409 40903 while
  an election may still mine); the local row then stays (Failed or Draft) so the owner can retry
  the delete.
- **Concurrent reads during publish**: `HydrateAsync` only promotes `Draft → Open` from a
  `completed` job plus a process read whose questions carry `upstreamId`s; it never deletes.
- **Wallet read with a narrow key**: 403 → balance null, no 502.
- **Upstream error body not JSON**: `Code`/`Data` stay null; raw message as today.

## Acceptance criteria
- [ ] AC1: No reference to `/subscription`, `/plans`, `features` endpoint or `create-process.sh`
      remains (`rg -n 'subscription|/plans|/features|create-process' src tests web/src README.md CLAUDE.md requests.http`
      finds nothing relevant).
- [ ] AC2: xUnit: create with a publish 402 deletes the upstream draft and leaves no local row; a
      publish job timeout returns 202 with a `Draft` proposal; a later read with a completed job
      promotes it to `Open` with upstream ids; a failed job marks it `Failed`.
- [ ] AC3: xUnit: the 502 body carries `code` and the readable sentence for 40175 and 40184.
- [ ] AC4: xUnit: `GET api/associations/{id}/price` counts only password holders when
      `requirePassword=true` and forwards `voters` to `/pricing`.
- [ ] AC5: xUnit: `GET api/integrator` returns balance from the wallet of the discovered
      `managedBy`, and null balance when there is no managed org or the wallet read 403s.
- [ ] AC6: xUnit: end-date extension sends RFC 3339 UTC, saves the new end on a completed job, and
      400s for a non-later date or a non-Open proposal.
- [ ] AC7: xUnit: a password hash that isn't 64 lowercase hex is a 400; a valid one reaches the
      upstream member `password`; `RequirePassword` sends `authFields ["memberNumber","password"]`.
- [ ] AC8: vitest: `castProcessVotes` with a password hashes it with the org address and passes
      `password` to `elections.authStep0`; without one, no `password` key is sent; all existing
      voting tests pass against `client.elections`.
- [ ] AC9: `dotnet test`, `npm test`, `npm run build` all green.
- [ ] AC10: README/CLAUDE.md/requests.http describe billing, password and end-date, and no longer
      describe plan gating.

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
