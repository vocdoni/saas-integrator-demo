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
cd web && npm run build             # production bundle (also what the web image builds)
./e2e.sh                            # full live flow; VTYPE=single|multiple|ranked; needs .env creds
```

EF migration (no local .NET SDK, so generate in the container; applied automatically on startup):
```bash
docker run --rm -v "$PWD":/src -w /src/src/HoaVoting.Api mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -lc 'dotnet tool install -g dotnet-ef >/dev/null 2>&1; export PATH=$PATH:/root/.dotnet/tools; \
            dotnet ef migrations add <Name>'
```

> **This branch targets the multi-question `/processes` API (saas-backend #571, unmerged).** A
> proposal is a **voting process container** with **N questions**, each its own on-chain election.
> The legacy singular `/process` + `/process/bundle` flow was **removed**. #571 is deployed nowhere
> yet — run a local saas-backend on the `feat/processes-api` branch and point `Vocdoni:BaseUrl` at it.

## Big picture (read multiple files to grasp)

- **A proposal = a multi-question process.** `Proposal` (container) has many `ProposalQuestion`s
  (`Models/Proposal.cs`), each mapping 1:1 to an on-chain election. `ProposalsController.Create`
  authors the whole process (`POST /processes`, census inline over the homeowners), publishes it as
  one async **batch** (`POST /processes/{id}/publish` → poll `/jobs/{id}`), then reads it back
  (`GET /processes/{id}`) to capture each question's on-chain `UpstreamId` + `Status`.
- **Voting is client-side and per question; the backend never casts or signs.** `web/src/voting.js`
  `castProcessVotes()` authenticates **once** per process (`POST /processes/{id}/auth/0`), then for
  each answered question CSP-signs (`POST /processes/{id}/sign` with that question's `upstreamId`) and
  relays one vote (`POST /vote`) — reusing `@vocdoni/api-voting`'s crypto (`EphemeralSigner`,
  `VotingClient`). The `/processes/*` CSP endpoints aren't in `@vocdoni/api-client`, so they're raw
  `fetch`. Bridge to the page: `VotingInfoResponse.ApiUrl` + `ChainId`.
- **Voting kinds are a cross-cut across four places.** `VotingType` (`single|multiple|ranked`,
  `Models/VotingType.cs`, JSON as a camelCase string via `JsonStringEnumConverter` in `Program.cs`) →
  a #571 question in `ProposalsController.ToQuestionRequest` (single=`singlechoice`,
  multiple=`multichoice`+`maxChoices`, ranked=raw `ballotProtocol` linear-weighted); the per-question
  ballot array is built in `voting.js`/`VotingPage.jsx` (`buildChoices`). See the
  **vocdoni-ballot-protocol** skill for the encoding.
- **Async everything via jobs.** Publish, question-status change, vote relay, bulk member add return a
  `jobId`; the client polls `GET /jobs/{id}` (fail-fast on `failed`). Publish is idempotent (200).
- **Status + tally reconcile.** `ProposalsController` List/Get + `VotingController` call
  `GET /processes/{id}/results` (per-question live on-chain `status` + `voteCount` + `results` matrix),
  refresh each question's `Status`, mark the proposal `Closed` when all questions ended, and pass the
  results into the response (matched by `UpstreamId`). Tallies render via `web/src/tally.js`
  `tallyCounts(results, kind)` + `QuestionResults.jsx` (single=`results[0]`, multiple=`results[i][1]`,
  ranked=Borda `Σ results[i][v]·v`). The public page also derives finished via `isFinished`.
- **Auth-only census.** Voters authenticate by **member number** (no 2FA); the process census is
  inline (`census: { authFields: ["memberNumber"], memberIds }`) — no separate census/group/publish.
- **One remaining #571 gap:** the question read has no **`chainId`** → configured via `Vocdoni:ChainId`,
  exposed to the page for vote signing.

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
