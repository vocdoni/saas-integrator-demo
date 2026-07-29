# Homeowners Voting Platform — .NET backend over the Vocdoni SaaS API

ASP.NET Core (.NET 10) backend for managing **homeowners' associations**. A single **admin**
creates associations, each with its own **owner**, who manages **homeowners** (the census),
creates **proposals**, and reads voting **results**. Built on the Vocdoni SaaS API.

> [!TIP]
> **Looking for the integrator docs?** 
>  - They now live on the Vocdoni **[developer portal](https://developer.vocdoni.io)**, start there for concepts, guides, and the end-to-end flow.
>  - For the exact endpoints and payloads, check the **[SaaS API swagger](https://github.com/vocdoni/saas-backend/blob/main/docs/swagger.yaml)**.

> [!WARNING]
> **This branch integrates the multi-question `/processes` API from [saas-backend #571](https://github.com/vocdoni/saas-backend/pull/571) (unmerged).**
> A **proposal is now a voting process with N questions**, each its own on-chain election; the legacy
> singular `/process` + `/process/bundle` flow was removed. #571 is **deployed nowhere yet** — run a
> local saas-backend on the `feat/processes-api` branch and set `VOCDONI_SERVER_URL` to point at it.
> Voting is per-question (CSP sign + relay client-side); per-question tallies (`voteCount`, `maxVoters`
> turnout, `results` matrix) come inline on the process read `GET /processes/{id}` once a question
> reaches `results` status ([saas-backend #596](https://github.com/vocdoni/saas-backend/pull/596)), and
> the `chainId` needed to sign votes also comes from the process read
> ([saas-backend #582](https://github.com/vocdoni/saas-backend/pull/582)).

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
Vocdoni:ApiToken                   integrator org's API key (Bearer); needs the managed:write scope
ConnectionStrings:Default          SQLite by default
```

The integrator org is resolved from the API key (the endpoints are path-less), so no integrator
address is configured.

**Prerequisites:**
1. A Vocdoni integrator account (free tier, SaaS dashboard)
2. An **API key** minted under that org (in the dashboard) with the **`managed:write`** scope (and
   `managed:read` if you run `e2e.sh`, which lists managed orgs for its adopt path)

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

- **Backend admin** (`SuperAdmin`) — create and list associations.
- **Association admin** (`Owner`) — manage the **memberbase** (add/remove homeowners + CSV import)
  and **voting processes** (create a **single choice**, **multiple choice**, or **ranked** ballot;
  view results, close). Voters authenticate by member number (no 2FA). Each proposal exposes a
  **Voting page** link. Results bars fill against the **census size** (turnout share, or the top
  score for ranked) and show the eligible count.
- **Public voting page** — `/processes/{processId}` is a no-login page (modeled on
  app.vocdoni.io's `/processes/:id`). It shows the ballot and lets a homeowner **cast a vote**:
  authenticate by member number, then pick one choice (single), several (multiple), or **drag to
  rank** the options (ranked), and submit. Casting runs entirely client-side against the SaaS API via
  `@vocdoni/integrator-sdk` (see `web/src/voting.js`) — CSP auth → CSP sign + build one envelope per
  question → relay the whole ballot in **one batch** (`POST /votes`) → poll the single job for the
  per-question outcomes. Page data comes from `GET /api/processes/{processId}`.

| Service | URL | Purpose |
|---------|-----|---------|
| `web`   | http://localhost:3000 | SPA (UI + public voting page) + `/api` proxy |
| `api`   | http://localhost:5095 | REST API directly (used by `e2e.sh`, `create-process.sh`, dev proxy) |

Local development with hot reload (Vite proxies `/api` → `:5095`, so keep the backend running):

```bash
docker compose up -d api        # or dotnet run, for the API
cd web && npm install && npm run dev   # http://localhost:5173
```

## Test

**Unit tests** (11/11 pass):

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

Or locally: `dotnet test`.

- `AuthorizationTests` — an Owner cannot access another's association.
- `VocdoniClientTests` — Bearer token is sent and failures surface without retry; member listing
  walks every page; async publish/status poll the job endpoint (202→poll, 200→idempotent, fail-fast).

**End-to-end test** against the live Vocdoni SaaS API:

```bash
./e2e.sh                  # admin login → association → memberbase → proposal → results → close
CSV=path/to.csv ./e2e.sh  # custom memberbase (default: memberbase-test.csv)
VTYPE=ranked ./e2e.sh     # voting type for the proposal: single (default) | multiple | ranked
```

Requires `.env` with valid Vocdoni credentials. The script:
- Creates a fresh association, reuses an existing one, or **adopts** an existing managed org if
  the integrator quota is full (`POST /api/associations/import`).
- Loads the **memberbase** from a CSV (`First Name,Member Number` or `First Name,Email,Member
  Number`) as homeowners — idempotent (skips if members already exist).
- Creates a proposal and waits for the async publish (~10–30s).

The proposal always uses a **CSP census** where voters authenticate by **member number** alone (no
2FA). Set `VTYPE` to create a single-choice (default), multiple-choice, or ranked ballot.

**Create a process on an existing census** (`create-process.sh`) — skips all setup
(org/owner/members) and just creates + publishes a new voting process on a census that already
exists. Talks directly to the Vocdoni API with the integrator token:

```bash
./create-process.sh                                   # discover org + last census from the app
ORG=0x.. CENSUS_ID=6a.. ./create-process.sh           # standalone (existing integrator org)
TITLE="Budget 2026" ./create-process.sh
```

Prints the new ProcessID (used for status/results) and its on-chain election id. Multiple processes
can share one census.

**How auth-only publishing works:** the plain `POST /census/{id}/publish` only accepts the 2FA
census types (`mail`/`sms`/`sms_or_mail`) and rejects auth-only (member-number) censuses with
`census type not found`. The backend instead publishes via a **member group** (`POST
/census/{id}/group/{groupid}/publish`), which supports auth-only censuses and populates participants
from the group. Note: each `Member Number` must be **unique** (the voting credential is
`hash(memberNumber)`) — duplicate member numbers fail at publish.

## Implementation Notes

- **Async publish:** publish and status-change return `202 + jobId`. `PublishVotingProcessAsync`
  polls `GET /jobs/{jobId}` until the job completes (failing fast on a failed job); publish is
  idempotent, so an already-published process returns `200` directly. `SetQuestionsStatusAsync`
  deliberately does **not** poll — closing a proposal is fire-and-forget, and the next process read
  reconciles the real status. Marked `ponytail:` — for production, move the poll to a background worker.
- **Integrator quota:** The free tier allows **1 managed organization**. Multiple associations
  require additional quota or a new integrator account. Deleting an association now frees the slot
  (`DELETE /integrator/organizations/{addr}` rolls back the integrator's usage counters).
- **Org/process addresses:** Sent/read as **hex strings** (Vocdoni's `HexBytes` wire format),
  not the int arrays the swagger nominally shows.
- **Swagger drift:** member deletion is `DELETE /organizations/{address}/members` (**plural**);
  the swagger's singular `/member` returns 404 on the deployed backend.
- **Census reuse:** multiple processes can target the same published census (see `create-process.sh`).
- **Client-side voting:** the web app casts ballots in the browser via `@vocdoni/integrator-sdk`
  (`@vocdoni/api-client` + `@vocdoni/api-voting`), which talk **only** to the SaaS API. The flow lives
  in `web/src/voting.js` (`castProcessVotes`): CSP auth **once** per process (member number) → per
  question, CSP-sign an ephemeral key and build the signed envelope → relay every envelope in one
  batch → poll the single job. The backend only exposes the SaaS API base URL and `chainId` to the
  page (`VotingInfoResponse.apiUrl` / `.chainId`); it never builds, signs, or relays ballots.
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
- **Voting types** (`votingType`): the owner picks one per proposal, mapped to the Vocdoni `voteType`:
  - **single** — `{maxCount:1, maxValue:N-1}`; ballot `[chosenIndex]`; results `results[0]` are
    per-choice counts.
  - **multiple** (approval) — `{maxCount:N, maxValue:1, uniqueChoices:false}`; ballot `[v0..vN-1]`
    (1 per pick); each option's count is `results[i][1]`. `uniqueChoices` **must** be false or
    multi-select ballots are rejected.
  - **ranked** (linear-weighted) — `{maxCount:N, maxValue:N-1, uniqueChoices:true}`; the voter
    **drags to sort**, top = best; ballot gives each option a unique rank value (`N-1-position`);
    results are read as a Borda score per option (`Σ results[i][v]·v`).
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
- `DELETE /api/associations/{id}` — remove the association + its proposals + owner login from the
  app, **and** delete the Vocdoni managed org via `DELETE /integrator/organizations/{addr}` (cascade:
  members, censuses, processes, bundles), reclaiming integrator quota. Returns **409** if the org
  still has active on-chain elections — close those proposals first. An org already gone upstream
  (404) is treated as success.

**Homeowners (admin or association owner):**
- `GET /api/associations/{id}/homeowners` — list members
- `POST /api/associations/{id}/homeowners` — add member (hits Vocdoni)
- `DELETE /api/associations/{id}/homeowners/{memberId}` — remove member

**Proposals (admin or association owner):**
- `POST /api/associations/{id}/proposals` — create a **multi-question voting process** in one call
  (`POST /processes` with the census inline over the homeowners → `POST /processes/{id}/publish`,
  polled → read back to capture each question's on-chain `upstreamId`, `status` and the `chainId`).
  There is no group/census/bundle dance. Body: `title`, `description`, `startDate`, `endDate`, and
  `questions[]`, each `{ title, choices[], kind }` with `kind` = `single` (default) | `multiple` |
  `ranked`. Voters always authenticate by member number (no 2FA).
- `GET /api/associations/{id}/proposals` — list
- `GET /api/associations/{id}/proposals/{pid}` — get one
- `POST /api/associations/{id}/proposals/{pid}/close` — end voting (all questions)

  List and get both hydrate from `GET /processes/{id}`: they refresh each question's live status,
  mark the proposal closed once every question has ended, and inline the per-question tally — so
  there is no separate results endpoint.

**Public (no auth):**
- `GET /api/processes/{processId}` — voting-page data for the public `/processes/{processId}` page:
  title, description, dates, status, and one entry per question (`title`, `choices[]`, `kind`,
  `upstreamId`, `status`, and best-effort on-chain `voteCount`, `maxVoters`, `results`). Also returns
  `apiUrl` (the SaaS API base URL) and `chainId` — the two things the page cannot derive — so it can
  sign and batch-relay votes client-side via the integrator SDK.
