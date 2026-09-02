# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

ASP.NET Core (.NET 10) backend + React SPA: a **Vocdoni integrator** for homeowners' associations.
Admin → associations (managed orgs); owners → homeowners (census), proposals (elections), results;
public voters cast ballots client-side. **`README.md` is the source of truth** for domain,
architecture, and the Vocdoni API mapping — read it before non-trivial work. Vocdoni quirks (swagger
drift, hex addresses, async jobs, per-question CSP) live in README → *Implementation Notes*.
Conceptual/integration docs now live on the Vocdoni developer portal (<https://developer.vocdoni.io>),
not in this repo.

## Layout

- `src/HoaVoting.Api/` — the backend.
  - `Controllers/` — thin; auth via `[Authorize(Roles=...)]`, ownership via `Authorization/AssociationAccess.cs`.
  - `Services/Vocdoni/` — all upstream calls. `IVocdoniClient` + `VocdoniClient` (typed `HttpClient`,
    Bearer token injected in `Program.cs`), `VocdoniModels.cs` wire DTOs.
  - `Services/Auth/` — JWT issuing. `Data/` — EF Core + SQLite, `DbSeeder` seeds the admin.
  - `Models/`, `Dtos/`, `Migrations/`.
- `tests/HoaVoting.Tests/` — xUnit; `WebApplicationFactory` integration tests (11 today).
- `web/` — React 19 + Vite SPA, nginx in prod. `src/api.js` = the app API client (JWT bearer);
  `src/voting.js` = **client-side vote casting** via the integrator SDK; `src/status.js` = shared
  `isFinished`; one component per page under `src/components/`.
- Roots: `e2e.sh`, `create-process.sh` (live-API scripts), `requests.http` (curl flow).

## Commands

```bash
docker compose up --build          # api :5095, web :3000 (proxies /api). Migrates + seeds on boot.
dotnet test                         # 11 tests. No local .NET? run in a container:
  docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
dotnet test --filter <TestName>     # single test
cd web && npm install && npm run dev # SPA hot reload :5173 (Vite proxies /api → :5095; needs api up)
cd web && npm test                  # vitest — covers voting.js (batch relay + partial failure)
cd web && npm run build             # production bundle (also what the web image builds)
./e2e.sh                            # full live flow; VTYPE=single|multiple|ranked; needs .env creds
```

EF migration (no local .NET SDK, so generate in the container; applied automatically on startup):
```bash
docker run --rm -v "$PWD":/src -w /src/src/HoaVoting.Api mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -lc 'dotnet tool install -g dotnet-ef >/dev/null 2>&1; export PATH=$PATH:/root/.dotnet/tools; \
            dotnet ef migrations add <Name>'
```

> **This app targets the multi-question `/processes` API (saas-backend #571, merged).** A proposal is
> a **voting process container** with **N questions**, each its own on-chain election. The legacy
> singular `/process` + `/process/bundle` flow was **removed**. `Vocdoni:ServerUrl` defaults to
> `https://saas-api-dev.vocdoni.net` (`.env.example` instead points at a local backend on :8080), so a
> hosted environment may lag a freshly merged backend change — when a call 404s, first establish which
> backend you are actually hitting.

## Big picture (read multiple files to grasp)

- **A proposal = a multi-question process.** `Proposal` (container) has many `ProposalQuestion`s
  (`Models/Proposal.cs`), each mapping 1:1 to an on-chain election. `ProposalsController.Create`
  authors the whole process (`POST /processes`, census inline over the homeowners), publishes it as
  one async **batch** (`POST /processes/{id}/publish` → poll `/jobs/{id}`), then reads it back
  (`GET /processes/{id}`) to capture each question's on-chain `UpstreamId` + `Status`.
- **Voting is client-side and batched; the backend never casts or signs.** `web/src/voting.js`
  `castProcessVotes()` authenticates **once** per process (`client.processes.authStep0`), then for
  each answered question CSP-signs (`client.processes.sign` with that question's `upstreamId`) and
  builds the signed envelope locally (`@vocdoni/api-voting`'s `EphemeralSigner` +
  `buildVoteTransaction`). All envelopes then go out in **one** `client.elections.voteBatch()` call
  (`POST /votes`, saas-backend #610) → **one** job. The batch is accepted or rejected as a unit, which
  is the point: relaying per question could leave a ballot half-voted. Per-vote outcomes come back
  index-aligned in `result.votes[]`, and because a job fails if *any* envelope failed, the
  `jobs.waitFor()` call catches `JobFailedError` to read the outcomes off the failed job rather than
  reporting blanket failure. Bridge to the page: `VotingInfoResponse.ApiUrl` + `ChainId`.
- **Voting kinds are a cross-cut across four places.** `VotingType` (`single|multiple|ranked`,
  `Models/VotingType.cs`, JSON as a camelCase string via `JsonStringEnumConverter` in `Program.cs`) →
  a #571 question in `ProposalsController.ToQuestionRequest` (single=`singlechoice`,
  multiple=`multichoice`+`maxChoices`, ranked=raw `ballotProtocol` linear-weighted); the per-question
  ballot array is built in `voting.js`/`VotingPage.jsx` (`buildChoices`). See the
  **vocdoni-ballot-protocol** skill for the encoding.
- **Open "Other" choice + voter memos (saas-backend #577, unmerged).** A **single-choice** question may
  mark one choice `openValue` (persisted as `ProposalQuestion.OpenChoiceIndex`, -1 = none). A voter who
  picks it must attach a free-text `memo`, which rides `VoteEnvelope.memo` via
  `@vocdoni/api-voting`'s `vote({…, memo})` (≤256 bytes, `MAX_MEMO_BYTES`). Memos come back **inline on
  `QuestionResults.memos`** on the process read, but **only for a manager/admin caller** and only at
  `results` — so `ProposalsController` surfaces them to the owner (`QuestionResponse.Memos`) while
  `VotingController` deliberately drops them (never public). Only single-choice is supported: the
  backend correlates each vote's selected value with the open choice's value, which only matches
  `votes = [chosenIndex]`.
- **Async everything via jobs.** Publish, question-status change, vote relay, bulk member add return a
  `jobId`; the client polls `GET /jobs/{id}` (fail-fast on `failed`). Publish is idempotent (200).
- **Status + tally reconcile.** `ProposalsController` List/Get (`HydrateAsync`) + `VotingController`
  read `GET /processes/{id}` (the process read carries per-question live on-chain `status` always, plus
  an inline `results` tally — `voteCount` + `maxVoters` + `results` matrix — resolved only once a
  question hits **`results`** status, saas-backend #596). They refresh each question's `Status`, mark
  the proposal `Closed` when all questions ended, and pass the inline tallies into the response (matched
  by `UpstreamId`). Tallies render via `web/src/tally.js` `tallyCounts(results, kind)` +
  `QuestionResults.jsx` (single=`results[0]`, multiple=`results[i][1]`, ranked=Borda `Σ results[i][v]·v`;
  `maxVoters` is the per-question turnout denominator). The public page also derives finished via
  `isFinished`. (The separate `GET /processes/{id}/results` endpoint exists but is unused — its shape
  dropped `status`, which we need every read.)
- **Auth-only census.** Voters authenticate by **member number** (no 2FA); the process census is
  inline (`census: { authFields: ["memberNumber"], memberIds }`) — no separate census/group/publish.
- **`chainId` comes from the process (#582).** The process read (`GET /processes/{id}`) carries the
  Vochain `chainId` votes must be signed against. `ProposalsController.Create` captures it from the
  publish read-back onto `Proposal.ChainId`; `VotingController` exposes it to the page for vote
  signing. (There is no `Vocdoni:ChainId` config anymore.)

## Conventions

- Secrets via `dotnet user-secrets` (local) or `.env` → compose env (`Vocdoni:ApiToken`, `Jwt:SigningKey`).
- Nullable + ImplicitUsings on. JWT claims unmapped: `sub` = name, standard role claim.
- Upstream failures throw `VocdoniApiException` → mapped to **502** in `Program.cs` (no retry). Keep it that way.
- Vocdoni org/process addresses are **hex strings** on the wire, not the int arrays the swagger shows.
- New Vocdoni call → add to `IVocdoniClient` + `VocdoniClient`, never inline in a controller.
- `ponytail:` comments mark deliberate shortcuts with their upgrade path — respect them.

## Upstream API (Vocdoni SaaS swagger)

Before changing any Vocdoni-facing code (`Services/Vocdoni/`, endpoints, the `.sh` scripts), consult
the **latest** swagger — the upstream API evolves and our wire DTOs must track it. Fetch a fresh copy
on demand (gitignored — don't commit a snapshot, it drifts):

```bash
curl -fsSL https://raw.githubusercontent.com/vocdoni/saas-backend/refs/heads/main/docs/swagger.yaml -o .vocdoni-swagger.yaml
```

Swagger is not gospel: the deployed backend diverges (plural `/members` delete, hex addresses,
auth-only group publish) — see README → *Implementation Notes*. Verify against a live call (`e2e.sh`)
when in doubt.

## Skills

- **`vocdoni-ballot-protocol`** — the ballot/result encoding behind the voting types (choices ↔ vote
  array ↔ tally matrix). Reach for it when touching `voteType`, ballot building, or `tallyCounts`.
- **`vocdoni-sdk`** — the older `@vocdoni/sdk`; this repo uses `@vocdoni/integrator-sdk` instead, but
  the SDK/CSP/bundle concepts still transfer when reasoning about `web/src/voting.js`.

Not applicable here: `vocdoni-go`/`go-*` (this is .NET, not Go), `davinci-sdk` (this targets the SaaS
API, not the Davinci protocol).
