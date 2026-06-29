# Homeowners Voting Platform — .NET backend over the Vocdoni SaaS API

ASP.NET Core (.NET 10) backend for managing **homeowners' associations**. A single **admin**
creates associations, each with its own **owner**, who manages **homeowners** (the census),
creates **proposals**, and reads voting **results**. Built on the
[Vocdoni SaaS API](https://raw.githubusercontent.com/vocdoni/saas-backend/refs/heads/main/docs/swagger.yaml).

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
- **The backend never builds or signs ballots.** The Vocdoni `/vote` endpoint only *relays an
  already-signed Vochain transaction*; ballot encoding + signing is client-side crypto, done in the
  voter's browser by the integrator SDK. Homeowners authenticate via Vocdoni's CSP/bundle flow.

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
Vocdoni:BaseUrl                    Vocdoni SaaS base URL (dev: https://saas-api-dev.vocdoni.net;
                                   stg: https://saas-api-stg.vocdoni.net)
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
  and **voting processes** (create single-choice or **multichoice** ballots with member-number or
  email-2FA auth, view results, close). Each proposal exposes a **Voting page** link. Results bars
  fill against the **census size** (turnout share) and show the eligible count.
- **Public voting page** — `/processes/{processId}` is a no-login page (modeled on
  app.vocdoni.io's `/processes/:id`). It shows the ballot and lets a homeowner **cast a vote**:
  authenticate by member number (plus an email OTP for 2FA censuses), pick a choice (or several, for
  multichoice), and submit. Casting runs entirely client-side against the SaaS API via
  `@vocdoni/integrator-sdk` (see `web/src/voting.js`) — CSP auth → CSP sign → relay `POST /vote` →
  poll for the vote nullifier. Page data comes from `GET /api/processes/{processId}`.

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
TWOFA=true ./e2e.sh       # email-2FA census instead of no-2FA (needs an Email column)
```

Requires `.env` with valid Vocdoni credentials. The script:
- Creates a fresh association, reuses an existing one, or **adopts** an existing managed org if
  the integrator quota is full (`POST /api/associations/import`).
- Loads the **memberbase** from a CSV (`First Name,Member Number` or `First Name,Email,Member
  Number`) as homeowners — idempotent (skips if members already exist).
- Creates a proposal and waits for the async publish (~10–30s).

By default the proposal uses a **CSP census with no 2FA** (`TWOFA=false`): voters authenticate by
**member number** alone. Run `TWOFA=true ./e2e.sh` for an email-2FA census (needs an `Email`
column in the CSV).

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

**How no-2FA publishing works:** the plain `POST /census/{id}/publish` only accepts the 2FA
census types (`mail`/`sms`/`sms_or_mail`) and rejects auth-only with `census type not found`. The
backend instead publishes via a **member group** (`POST /census/{id}/group/{groupid}/publish`),
which supports auth-only censuses and populates participants from the group. Note: with auth-only,
each `Member Number` must be **unique** (the voting credential is `hash(memberNumber)`) — duplicate
member numbers fail at publish.

## Implementation Notes

- **Async publish:** publish and status-change return `202 + jobId`; `PublishProcessAsync` /
  `SetProcessStatusAsync` poll `GET /jobs/{jobId}` until the job completes (failing fast on a failed
  job). Publish is idempotent: an already-published process returns `200` directly. Marked
  `ponytail:` — for production, move the poll to a background worker.
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
  in `web/src/voting.js` (`castVote`): bundle CSP auth (member number, +OTP for 2FA) → CSP sign over an
  ephemeral key → relay `POST /vote` → poll the job for the nullifier. The backend only exposes the
  SaaS API base URL to the page (`VotingInfoResponse.apiUrl`); it never builds or signs ballots.
- **Turnout / census size:** result bars fill against the **eligible voter count** (the published
  census size), read from `GET /process/{id}` (`census.size`) and surfaced as `censusSize` on the
  results + public voting payloads. The page shows "N eligible" alongside the vote count.
- **Multichoice (approval):** `allowMultiple` proposals use approval voting — `voteType
  {maxCount:N, maxValue:1, uniqueChoices:false}` (a 0/1 field per option; `uniqueChoices` **must** be
  false or multi-select ballots are rejected). The ballot is `[v0..vN-1]`; results have one field per
  option (`[#voted-0, #voted-1]`), so each option's count is `results[i][1]` (not `results[0]`).
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
- `POST /api/associations/{id}/proposals` — create (group → census → group-publish → process →
  publish → **bundle**). The published process is wrapped in a new process bundle (stored as
  `vocdoniBundleId`) for the CSP voting flow. Body: `title`, `description`, `choices[]`,
  `startDate`, `endDate`, `allowMultiple` (default false), `twoFactorAuth` (default false → CSP auth
  by member number; true → email OTP, needs member emails).
- `GET /api/associations/{id}/proposals` — list
- `GET /api/associations/{id}/proposals/{pid}` — get one
- `POST /api/associations/{id}/proposals/{pid}/close` — end voting
- `GET /api/associations/{id}/proposals/{pid}/results` — read tally

**Public (no auth):**
- `GET /api/processes/{processId}` — voting-page data for the public `/processes/{processId}` page:
  title, description, choices, dates, status, `allowMultiple`, and (best-effort) on-chain
  `voteCount`, `results`, and `censusSize`. Also returns `bundleId` and `apiUrl` (the SaaS API base
  URL) so the page can cast votes client-side via the integrator SDK.
