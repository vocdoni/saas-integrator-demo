# HOA Voting — .NET backend over the Vocdoni SaaS API

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
| Association   | Managed organization (`POST /organizations/{parent}/managed`) |
| Homeowner     | Org member + census participant                      |
| Proposal      | Member group → census → process (election) + results |

## Scope

- **In scope:** associations, owners, homeowners/census, proposals (create/close), results.
- **Out of scope:** vote casting. The Vocdoni `/vote` endpoint only *relays an already-signed
  Vochain transaction*; ballot encoding + signing is client-side crypto done by the Vocdoni
  **JS SDK**. A frontend casts votes; homeowners authenticate via Vocdoni's CSP/bundle flow.

## Identity

- The backend owns app identity: hardcoded **admin** (seeded from env/config) who registers
  associations, plus per-association **Owners** who log in here and manage their association.
- Homeowners are **Vocdoni org members only**, not app users. They authenticate to *vote* via
  Vocdoni's CSP flow (client-side, in the frontend).
- All Vocdoni calls use a single **integrator API key** (`Authorization: Bearer`). Associations
  are created as **managed orgs** under the integrator (`POST /organizations/{integratorAddress}/managed`).

## Configuration

`src/HoaVoting.Api/appsettings.json` (use **user-secrets** for secrets):

```
Jwt:SigningKey                     long random secret (>= 32 chars)
Admin:Email / Admin:Password       seeds the admin on startup
Vocdoni:BaseUrl                    Vocdoni SaaS base URL (dev: https://saas-api-dev.vocdoni.net;
                                   stg: https://saas-api-stg.vocdoni.net)
Vocdoni:ApiToken                   integrator org's API key (Bearer)
Vocdoni:IntegratorAddress          address of the integrator org the key belongs to (required)
ConnectionStrings:Default          SQLite by default
```

**Prerequisites:**
1. A Vocdoni integrator account (free tier, SaaS dashboard)
2. Your integrator org's **address** (visible in dashboard)
3. An **API key** minted under that org (also in dashboard)

```bash
cd src/HoaVoting.Api
dotnet user-secrets init
dotnet user-secrets set "Vocdoni:ApiToken" "your-api-key"
dotnet user-secrets set "Vocdoni:IntegratorAddress" "0x..."
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

## Test

**Unit tests** (5/5 pass):

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

Or locally: `dotnet test`.

- `AuthorizationTests` — an Owner cannot access another's association.
- `VocdoniClientTests` — verifies Bearer token is sent; failures surface without retry.

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

Prints the new on-chain process id. Multiple processes can share one census.

**How no-2FA publishing works:** the plain `POST /census/{id}/publish` only accepts the 2FA
census types (`mail`/`sms`/`sms_or_mail`) and rejects auth-only with `census type not found`. The
backend instead publishes via a **member group** (`POST /census/{id}/group/{groupid}/publish`),
which supports auth-only censuses and populates participants from the group. Note: with auth-only,
each `Member Number` must be **unique** (the voting credential is `hash(memberNumber)`) — duplicate
member numbers fail at publish.

## Implementation Notes

- **Async publish:** `PublishProcessAsync` polls the draft process until the on-chain election
  id is assigned. Marked `ponytail:` — for production, move this to a background job + status field.
- **Integrator quota:** The free tier allows **1 managed organization**. Multiple associations
  require additional quota or a new integrator account. No delete-managed-org endpoint exists.
- **Org/process addresses:** Sent/read as **hex strings** (Vocdoni's `HexBytes` wire format),
  not the int arrays the swagger nominally shows.
- **Swagger drift:** member deletion is `DELETE /organizations/{address}/members` (**plural**);
  the swagger's singular `/member` returns 404 on the deployed backend.
- **Census reuse:** multiple processes can target the same published census (see `create-process.sh`).
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

**Homeowners (admin or association owner):**
- `GET /api/associations/{id}/homeowners` — list members
- `POST /api/associations/{id}/homeowners` — add member (hits Vocdoni)
- `DELETE /api/associations/{id}/homeowners/{memberId}` — remove member

**Proposals (admin or association owner):**
- `POST /api/associations/{id}/proposals` — create (group → census → group-publish → process → publish).
  Body: `title`, `description`, `choices[]`, `startDate`, `endDate`, `allowMultiple` (default false),
  `twoFactorAuth` (default false → CSP auth by member number; true → email OTP, needs member emails).
- `GET /api/associations/{id}/proposals` — list
- `GET /api/associations/{id}/proposals/{pid}` — get one
- `POST /api/associations/{id}/proposals/{pid}/close` — end voting
- `GET /api/associations/{id}/proposals/{pid}/results` — read tally
