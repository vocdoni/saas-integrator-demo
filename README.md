# Homeowners Voting Platform — .NET backend over the Vocdoni SaaS API

ASP.NET Core (.NET 10) backend for managing **homeowners' associations**. A single **admin**
creates associations, each with its own **owner**, who manages **homeowners** (the census),
creates **proposals**, and reads voting **results**. Built on the Vocdoni SaaS API.

> [!TIP]
> **Looking for the integrator docs?** 
>  - They now live on the Vocdoni **[developer portal](https://developer.vocdoni.io)**, start there for concepts, guides, and the end-to-end flow.
>  - For the exact endpoints and payloads, check the **[SaaS API swagger](https://github.com/vocdoni/saas-backend/blob/main/docs/swagger.yaml)**.

> [!NOTE]
> **Needs a saas-backend that includes [#760](https://github.com/vocdoni/saas-backend/pull/760) (member
> password), [#819](https://github.com/vocdoni/saas-backend/pull/819) (pay-per-process billing),
> [#844](https://github.com/vocdoni/saas-backend/pull/844) (extend end date) and
> [#855](https://github.com/vocdoni/saas-backend/pull/855) (plan endpoints removed)** — all on `main`.
> A **proposal is a voting process with N questions** (#571), each its own on-chain election. Voting is
> client-side (CSP sign + batch relay); per-question tallies come inline on the process read
> `GET /processes/{id}` once a question reaches `results` (#596), along with the `chainId` (#582). A
> single-choice question may carry an open **"Other" choice** whose voters attach a free-text memo
> (#577), shown to the owner only.

## Architecture

The backend is a **Vocdoni integrator** — it creates and manages homeowners' associations as
**managed organizations** under a parent integrator org. One hardcoded admin (seeded from env)
registers associations + their owner; owners log in and manage their association (homeowners,
proposals, results). All Vocdoni calls use a single API token (Bearer auth) scoped to the
integrator org.

## Domain → Vocdoni mapping

| App concept   | Vocdoni                                              |
|---------------|------------------------------------------------------|
| Association   | Managed organization (`POST /integrator/organizations`) |
| Homeowner     | Org member + census participant                      |
| Proposal      | Member group → census → process (election) + results |

## Scope

- **In scope:** associations, owners, homeowners/census, proposals (create/close), results, and
  **vote casting** — the web app casts ballots client-side **through the Vocdoni SaaS API** via
  [`@vocdoni/integrator-sdk`](https://github.com/vocdoni/integrator-sdk) (no direct Vochain calls).
- **The backend never builds or signs ballots.** The Vocdoni `/votes` endpoint only *relays
  already-signed Vochain transactions*; ballot encoding + signing is client-side crypto, done in the
  voter's browser by the integrator SDK. Homeowners authenticate via Vocdoni's CSP flow.

## Identity

- The backend owns app identity: hardcoded **admin** (seeded from env/config) who registers
  associations, plus per-association **Owners** who log in here and manage their association.
- Homeowners are **Vocdoni org members only**, not app users. They authenticate to *vote* via
  Vocdoni's CSP flow, implemented client-side in the web app (`web/src/voting.js`).
- All Vocdoni calls use a single **integrator API key** (`Authorization: Bearer`). Associations
  are created as **managed orgs** under the integrator (`POST /integrator/organizations`; the
  integrator org is resolved from the API key, so the path carries no address).

## Configuration

`src/HoaVoting.Api/appsettings.json` (use **user-secrets** for secrets):

```
Jwt:SigningKey                     long random secret (>= 32 chars)
Admin:Email / Admin:Password       seeds the admin on startup
Vocdoni:ServerUrl                  SaaS base URL the backend calls (dev: https://saas-api-dev.vocdoni.net;
                                   stg: https://saas-api-stg.vocdoni.net)
Vocdoni:BrowserUrl                 browser-facing SaaS URL for the voting page (defaults to ServerUrl)
Vocdoni:ApiToken                   integrator org's API key (Bearer); scopes below
ConnectionStrings:Default          SQLite by default
```

The integrator org is resolved from the API key (the endpoints are path-less), so no integrator
address is configured.

**Prerequisites:**
1. A Vocdoni integrator account (free tier, SaaS dashboard)
2. An **API key** minted under that org (in the dashboard) with the scopes **`managed:write`**,
   **`managed:read`**, **`quota:read`** (integrator status + wallet), **`voting:write`** and
   **`members:write`**
3. A **topped-up integrator wallet** (in the Vocdoni portal) once you need more than one association
   or a proposal over 25 voters: publishing is pay-per-process and paid from that wallet

```bash
cd src/HoaVoting.Api
dotnet user-secrets init
dotnet user-secrets set "Vocdoni:ApiToken" "your-api-key"
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)"
```

## Run (Docker)

No local .NET SDK needed.

```bash
cp .env.example .env        # fill in Vocdoni credentials and JWT key
docker compose up --build
```

API runs on http://localhost:5095. Migrations apply and the admin is seeded on startup; the
SQLite db persists in the `hoa-data` volume. OpenAPI spec at `/openapi/v1.json` (Development).

**Full walkthrough:** See `requests.http` for a curl-ready flow: admin login → create
association → owner login → add homeowners → create proposal → read results.

## Web app

A React (Vite) SPA lives in `web/`, served by its own **`web`** compose service (nginx) on
**http://localhost:3000**. nginx serves the SPA and proxies `/api` → the `api` service, so the
browser stays same-origin (no CORS). `docker compose up --build` brings up both services. Two roles:

- **Backend admin** (`SuperAdmin`) — create and list associations; sees the integrator **wallet
  balance** and whether another association can be created before a top-up.
- **Association admin** (`Owner`) — manage the **memberbase** (add/remove homeowners + CSV import,
  set a homeowner's **password**) and **voting processes** (create a **single choice**, **multiple
  choice**, **ranked**, or **cumulative/quadratic** ballot; optionally **anonymous** and/or
  **password-protected**; see the **estimated price** before creating; view results, close,
  **extend the end date**; edit each question's **voter eligibility** live; **retry** or **delete** a
  proposal whose publish failed or is stuck). Voters authenticate by member number (no 2FA), plus
  their password when the proposal requires one. Each live proposal exposes a **Voting page** link.
  Results bars fill against the **census size** (turnout share, or the top score for
  ranked/cumulative) and show the eligible count.
- **Public voting page** — `/processes/{processId}` is a no-login page (modeled on
  app.vocdoni.io's `/processes/:id`). It shows the ballot and lets a homeowner **cast a vote**:
  authenticate by member number (and password, hashed in the browser), then pick one choice (single), several (multiple), **drag to
  rank** the options (ranked), or **distribute credits** (cumulative/quadratic), and submit. Casting
  runs entirely client-side against the SaaS API via `@vocdoni/integrator-sdk` (see
  `web/src/voting.js`) — CSP auth → sign the whole ballot in **one call** (`sign-batch`, or the
  blind-signature flow on an anonymous process) → build one envelope per question → relay the whole
  ballot in **one batch** (`POST /votes`) → poll the single job for the per-question outcomes. Page
  data comes from `GET /api/processes/{processId}`.

| Service | URL | Purpose |
|---------|-----|---------|
| `web`   | http://localhost:3000 | SPA (UI + public voting page) + `/api` proxy |
| `api`   | http://localhost:5095 | REST API directly (used by `e2e.sh`, dev proxy) |

Local development with hot reload (Vite proxies `/api` → `:5095`, so keep the backend running):

```bash
docker compose up -d api        # or dotnet run, for the API
cd web && npm install && npm run dev   # http://localhost:5173
```

## Test

**Backend tests** (90/90 pass):

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

Or locally: `dotnet test`.

- `AuthorizationTests` — an Owner cannot access another's association.
- `VocdoniClientTests` — Bearer token is sent and failures surface without retry; member listing
  walks every page; publish returns its job without polling (200 → null); bounded job wait
  (completed / failed / pending); end date as RFC 3339 UTC; pricing, integrator, wallet and member
  upsert on the wire; question-census updates (200 vs 202+job vs 409), `census.anonymous`.
- `QuestionMappingTests` — the demo kind → named #638 question type mapping (ranked sends no
  typeSetup; cumulative carries budget + costExponent) and the 40173 eligibility-conflict parse.
- `UpstreamErrorsTests` — readable 502 details for the billing/limit codes.
- `MigrationTests` — migrations cover the model (EF refuses pending changes).
- Controller tests over SQLite in-memory + `FakeVocdoniClient` (`ControllerTestBase`):
  `ProposalLifecycleTests` (resilient create, reconcile, delete/retry/close, end date) and
  `BillingAndPasswordTests` (price, integrator wallet, member password hash).

**Web tests** (18/18 pass, vitest): `cd web && npm test` — `castProcessVotes` orchestration: one
batch sign (plain or blind), upstreamId matching, per-ballot sign failures, one relay, partial
failure, and the member password hashed with the org address before step 0.

**End-to-end test** against the live Vocdoni SaaS API:

```bash
./e2e.sh                  # admin login → association → memberbase → proposal → results → close
CSV=path/to.csv ./e2e.sh  # custom memberbase (default: memberbase-test.csv)
VTYPE=ranked ./e2e.sh     # voting type for the proposal: single (default) | multiple | ranked
```

Requires `.env` with valid Vocdoni credentials. The script:
- Prints the integrator status (`GET /api/integrator`: wallet balance, whether another association
  can be created).
- Creates a fresh association, reuses an existing one, or **adopts** an existing managed org when
  the integrator must top up before creating another (402 code 40184; `POST /api/associations/import`).
- Loads the **memberbase** from a CSV (`First Name,Member Number` or `First Name,Email,Member
  Number`) as homeowners — idempotent (skips if members already exist).
- Creates a proposal; on `202` (publish still running) it polls the proposal until `Open` (fails on
  `Failed`, gives up after 15 min).

The proposal always uses a **CSP census** where voters authenticate by **member number** alone (no
2FA). Set `VTYPE` to create a single-choice (default), multiple-choice, or ranked ballot. Each
`Member Number` must be **unique** (the voting credential is derived from it).

## Implementation Notes

- **Publish states:** publish returns `202 + jobId` (or `200` when already published). Pay-per-process
  publish jobs can stay pending from minutes to hours (the service retries transient chain failures),
  so the proposal row is saved as `Draft` **before** publishing and the job id is stored as soon as it
  is known. The create request waits a bounded ~40 s (`WaitForJobAsync`, under nginx's 60 s proxy
  timeout): `201 Open` if the job completed, `202 Draft` if it is still running, `502` if it failed.
  A refused publish deletes the upstream draft (and the row); if upstream will not delete it the row
  becomes `Failed`. A failed job always leaves a `Failed` row with `publishError` — upstream refuses
  to delete a draft whose publish submitted elections (409 40903/40904) and publishing it again
  adopts them at no new charge, hence **Retry publish**. Every list/get (and the public page) runs the
  same reconcile: promote a `Draft` whose process read is `published` with every question's
  `upstreamId`, or fail it from its job. `SetQuestionsStatusAsync` deliberately does **not** poll —
  closing is fire-and-forget. Marked `ponytail:` — for production, move the polls to a background worker.
- **Pay-per-process billing (saas-backend #819):** publishing debits the integrator's wallet (free up
  to 25 voters). Refusals surface as readable 502 details: 40175 wallet short (with the amounts), 40183
  spend cap, 40176 over 50 000 voters. The owner sees the estimate first (`GET /pricing`, via
  `GET /api/associations/{id}/price`). Until the wallet holds a top-up the integrator may create only
  one managed org (402 40184). Limits: 15 questions per process, 90-day processes, 20 drafts per org.
- **Member password (saas-backend #760):** a proposal with `requirePassword` sets census
  `authFields: ["memberNumber","password"]`; homeowners without a password are left out (`missingData`
  on the create response) and are not added if one is set later. Passwords are hashed **in the
  browser** with `hashMemberPassword(orgAddress, password)` from `@vocdoni/api-client` (needs HTTPS or
  localhost); the backend only forwards the 64-hex hash and never stores it. A wrong password answers
  like an unknown member (40802); step 0 is throttled (40017).
- **Extend end date (saas-backend #844):** `PUT /processes/{id}/endDate` enqueues a job; the demo
  saves the new end once it completed, and otherwise picks upstream's `endDate` up on the next read.
- **Org/process addresses:** Sent/read as **hex strings** (Vocdoni's `HexBytes` wire format),
  not the int arrays the swagger nominally shows.
- **Swagger drift:** member deletion is `DELETE /organizations/{address}/members` (**plural**);
  the swagger's singular `/member` returns 404 on the deployed backend.
- **Client-side voting:** the web app casts ballots in the browser via `@vocdoni/integrator-sdk`
  (`@vocdoni/api-client` + `@vocdoni/api-voting`), which talk **only** to the SaaS API. The flow lives
  in `web/src/voting.js` (`castProcessVotes`): CSP auth **once** per process (member number) → sign
  the **whole ballot in one CSP call** (one fresh ephemeral key per question, created before signing)
  → build the signed envelopes → relay them in one batch → poll the single job. The backend only
  exposes the SaaS API base URL, `chainId` and the `anonymous` flag to the page
  (`VotingInfoResponse.apiUrl` / `.chainId` / `.anonymous`); it never builds, signs, or relays ballots.
- **Batch ballot signing (saas-backend #634):** `POST /processes/{id}/sign-batch` signs every
  question's ephemeral address in one round trip (`{authToken, ballots:[{upstreamId, address}]}`).
  Authorization is all-or-nothing (a 401 signs nothing); per-ballot failures come back **inline**
  (`{upstreamId, code, error}`) and the client matches results **by `upstreamId`**, never by index.
  A failed ballot becomes a failed outcome row and the rest still relay; there is **no automatic
  re-sign** — a successful re-sign consumes the election's finite vote-overwrite budget.
- **Anonymous voting (saas-backend #641, blind CSP):** creating a proposal with `anonymous: true`
  sets `census.anonymous` on the process, swapping the census to blind signatures
  (`OFF_CHAIN_CA_V2`): the CSP signs a **blinded** CA bundle, so it can never link the voter it
  authenticated to the ballot that lands on chain. The client flow (all inside
  `@vocdoni/api-voting`'s `signBlindCspBallots()`) is two rounds — fetch a per-election R point
  (`blind-point`), blind the bundle locally, get it signed (`blind-sign`), unblind — and the envelope
  carries an `ECDSA_BLIND_PIDSALTED` proof. The CSP-pinned `weight` must be passed back **verbatim**
  (it salts the key the chain verifies). Auth and the batch relay are unchanged; the plain sign
  endpoints reject an anonymous census and vice versa. Available on every plan (#718); a service-wide
  switch can refuse it at create (400 40037 "anonymous voting is temporarily unavailable"), shown
  verbatim.
- **Live question eligibility (saas-backend #621):** a published question's voter subset can be
  edited via `PUT /processes/{pid}/questions/{qid}/census` with the **complete** desired member-id
  list (`[]` reopens it to the whole census; the response reports `eligible/added/removed`, and a
  `202 + jobId` means an on-chain census resize was enqueued). Restricting a question never strips a
  voter who already holds a CSP signature while the election runs — the backend answers **409 code
  40173** with `data.signedMemberIds`, which the admin UI maps to homeowner names.
- **Batch vote relay (saas-backend #610):** a proposal is a multi-question process and every question
  is its own on-chain election, so a ballot is N vote transactions. They go out together via
  `POST /votes`, which the backend validates and enqueues **all or nothing** — a rejected batch relays
  nothing, so the voter can fix and retry without being half-voted. (Relaying one `POST /vote` per
  question left exactly that window: an early question on chain, a later one failed, no rollback.)
  The call returns **one** `jobId` for the whole batch; `GET /jobs/{jobId}` reports `result.votes[]`,
  one entry per envelope **in request order**, each with `processId`/`nullifier` (readable while
  pending) plus `voteID` once mined or `error` if that vote was rejected. At most 100 votes per batch,
  which a process caps at anyway — no chunking needed.
- **Partial failure is representable.** The batch is atomic on *submission*, not on *settlement*:
  each envelope is queued separately, so one vote can fail on chain while its siblings land. Such a
  job ends `failed`, and `jobs.waitFor()` throws `JobFailedError` — but the failed job still carries
  the per-vote truth, so `voting.js` catches it and reads `e.job.result.votes` rather than reporting a
  blanket failure. The voting page renders one ✓/✗ row per question.
- **Turnout / census size:** result bars fill against the **eligible voter count**, read per question
  as `maxVoters` from the inline tally on `GET /processes/{id}` (saas-backend #596) and surfaced on
  the results + public voting payloads. The page shows "N eligible" alongside the vote count.
- **Voting types** (`kind`): the owner picks one per question, mapped to a **named** question type
  (saas-backend #638 — the backend derives the on-chain ballot protocol from the type, and rejects a
  supplied protocol that contradicts it):
  - **single** — `type: singlechoice` (min/max 1); ballot `[chosenIndex]`; results `results[0]` are
    per-choice counts.
  - **multiple** (approval) — `type: multichoice` + `typeSetup.maxChoices: N`; ballot `[v0..vN-1]`
    (1 per pick); each option's count is `results[i][1]`.
  - **ranked** (linear-weighted) — `type: ranked` with **no typeSetup** (the choices define the whole
    protocol; a non-empty typeSetup is rejected); the voter **drags to sort**, top = best; ballot
    gives each option a unique rank value (`N-1-position`); results are read as a Borda score per
    option (`Σ results[i][v]·v`).
  - **cumulative** (incl. quadratic) — `type: cumulative` + `typeSetup{budget, costExponent}`
    (`1` linear, `2` quadratic); the voter distributes credits, ballot `[v0..vN-1]` (credits per
    option) with cost `Σ v^costExponent ≤ budget`; results are total credits per option
    (index-weighted, same fold as ranked).
- **Single admin:** The hardcoded admin (from env) is the only one who can register associations.
  Multiple admins would require a lookup table; add when needed.

## API Endpoints

All endpoints except `/api/auth/login` require a valid JWT bearer token.

**Auth (no auth):**
- `POST /api/auth/login` — app login (admin or owner) → JWT

**Associations (admin only):**
- `POST /api/associations` — create association + owner (creates a Vocdoni managed org)
- `POST /api/associations/import` — adopt an existing managed org (no Vocdoni create call)
- `GET /api/associations` — list all
- `GET /api/associations/{id}` — get one (admin or its owner)
- `GET /api/associations/{id}/price?requirePassword=bool` — estimated pay-per-process price over the
  current homeowners (only those with a password when `requirePassword`): `{voters, totalCents,
  currency, quoteRecommended, quoteRequired}` (admin or its owner)
- `GET /api/integrator` — `{canCreateManagedOrg, managedOrgsCreated, balanceCents?, currency?}`; the
  balance is null when unknown (no managed org yet, or the key lacks `quota:read`)
- `DELETE /api/associations/{id}` — remove the association + its proposals + owner login from the
  app, **and** delete the Vocdoni managed org via `DELETE /integrator/organizations/{addr}` (cascade:
  members, censuses, processes, bundles). This does not free the one-association-before-top-up slot: upstream counts deleted organizations too. Returns **409** if the org
  still has active on-chain elections — close those proposals first. An org already gone upstream
  (404) is treated as success.

**Homeowners (admin or association owner):**
- `GET /api/associations/{id}/homeowners` — list members
- `POST /api/associations/{id}/homeowners` — add member (hits Vocdoni); optional `passwordHash`
- `PUT /api/associations/{id}/homeowners/{memberId}/password` — `{passwordHash}` (64 lowercase hex,
  computed in the browser); 404 unless `memberId` is a current member
- `DELETE /api/associations/{id}/homeowners/{memberId}` — remove member

**Proposals (admin or association owner):**
- `POST /api/associations/{id}/proposals` — create a **multi-question voting process** in one call
  (`POST /processes` with the census inline over the homeowners → local `Draft` row →
  `POST /processes/{id}/publish` → bounded job wait → read back to capture each question's on-chain
  `upstreamId`, `status` and the `chainId`). **201** `Open`, **202** `Draft` (still publishing), or
  **502** (refused / job failed). Body: `title`, `description`, `startDate`, `endDate` (≤ 90 days),
  `anonymous` (blind-CSP voting), `requirePassword`, and `questions[]` (≤ 15), each
  `{ title, choices[], kind, budget?, costExponent? }` with `kind` = `single` (default) | `multiple` |
  `ranked` | `cumulative` (which requires `budget` ≥ 1 and `costExponent` 1|2). The response carries
  `missingData` (homeowners left out of the census) and, on `Failed`, `publishError`.
- `GET /api/associations/{id}/proposals` — list
- `GET /api/associations/{id}/proposals/{pid}` — get one
- `POST /api/associations/{id}/proposals/{pid}/close` — end voting (all questions); 409 unless `Open`
- `PUT /api/associations/{id}/proposals/{pid}/end-date` — `{endDate}`, later than the current one,
  `Open` only: 200 done, 202 still running on chain, 502 failed
- `POST /api/associations/{id}/proposals/{pid}/publish` — retry the publish of a `Failed` proposal
  (409 otherwise)
- `DELETE /api/associations/{id}/proposals/{pid}` — delete a `Draft` or `Failed` proposal and its
  upstream draft (409 otherwise; 502 and the row stays when upstream refuses)
- `PUT /api/associations/{id}/proposals/{pid}/questions/{qid}/eligibility` — replace the question's
  voter subset on the live process (`{memberIds}` = complete list, `[]` = everyone); 409 with
  `signedMemberIds` when a voter who already holds a ballot signature would lose eligibility

  List and get both reconcile from `GET /processes/{id}`: they settle `Draft` proposals, copy the
  upstream end date, refresh each question's live status, mark an `Open` proposal closed once every
  question has ended, and inline the per-question tally — so there is no separate results endpoint.

**Public (no auth):**
- `GET /api/processes/{processId}` — voting-page data for the public `/processes/{processId}` page:
  title, description, dates, status, `anonymous`, `requirePassword`, `orgAddress` (for the password
  hash), and one entry per question (`title`, `choices[]`,
  `kind`, `budget`/`costExponent` for cumulative, `upstreamId`, `status`, and best-effort on-chain
  `voteCount`, `maxVoters`, `results`). Also returns `apiUrl` (the SaaS API base URL) and `chainId` —
  the things the page cannot derive — so it can sign (batch or blind) and batch-relay votes
  client-side via the integrator SDK. 404 while the proposal is `Draft` or `Failed`.
