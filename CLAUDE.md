# CLAUDE.md

ASP.NET Core (.NET 10) backend + React SPA: a **Vocdoni integrator** for homeowners'
associations. Admin → associations (managed orgs); owners → homeowners (census), proposals
(elections), results. **`README.md` is the source of truth** for domain, architecture, and the
Vocdoni API mapping — read it before non-trivial work. Vocdoni quirks (swagger drift, no-2FA
group publish, hex addresses, async publish) live in README → *Implementation Notes*.

## Layout

- `src/HoaVoting.Api/` — the backend.
  - `Controllers/` — thin; auth via `[Authorize(Roles=...)]`, ownership via `Authorization/AssociationAccess.cs`.
  - `Services/Vocdoni/` — all upstream calls. `IVocdoniClient` + `VocdoniClient` (typed `HttpClient`,
    Bearer token injected in `Program.cs`), `VocdoniModels.cs` wire DTOs.
  - `Services/Auth/` — JWT issuing. `Data/` — EF Core + SQLite, `DbSeeder` seeds the admin.
  - `Models/`, `Dtos/`, `Migrations/`.
- `tests/HoaVoting.Tests/` — xUnit; `WebApplicationFactory` integration tests.
- `web/` — React 19 + Vite SPA, nginx in prod. `src/api.js` is the API client; one component per page.
- Roots: `e2e.sh`, `create-process.sh` (live-API scripts), `requests.http` (curl flow),
  `docs/integration-guide.md`.

## Commands

```bash
docker compose up --build          # api :5095, web :3000 (proxies /api). Migrates + seeds on boot.
dotnet test                         # or: docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
cd web && npm install && npm run dev # SPA hot reload :5173 (needs api running)
./e2e.sh                            # full live flow; needs .env with Vocdoni creds
```

EF migration: `cd src/HoaVoting.Api && dotnet ef migrations add <Name>` (applied on startup).

## Conventions

- Secrets via `dotnet user-secrets` (local) or `.env` → compose env (`Vocdoni:ApiToken`, `Jwt:SigningKey`).
  Never hardcode or commit them.
- Nullable + ImplicitUsings on. JWT claims unmapped: `sub` = name, standard role claim.
- Upstream failures throw `VocdoniApiException` → mapped to **502** in `Program.cs` (no retry). Keep it that way.
- Vocdoni org/process addresses are **hex strings** on the wire, not int arrays.
- New Vocdoni call → add to `IVocdoniClient` + `VocdoniClient`, not inline in a controller.
- `ponytail:` comments mark deliberate shortcuts with their upgrade path — respect them.

## Upstream API (Vocdoni SaaS swagger)

Before changing any Vocdoni-facing code (`Services/Vocdoni/`, endpoints, the `.sh` scripts),
consult the **latest** swagger — the upstream API evolves and our wire DTOs must track it:

> https://github.com/vocdoni/saas-backend/blob/main/docs/swagger.yaml

Fetch a fresh local copy on demand (gitignored — don't commit a snapshot, it drifts):

```bash
curl -fsSL https://raw.githubusercontent.com/vocdoni/saas-backend/refs/heads/main/docs/swagger.yaml -o .vocdoni-swagger.yaml
```

Swagger is not gospel: the deployed backend diverges in places (plural `/members` delete, hex
addresses, no-2FA group publish) — see README → *Implementation Notes*. Verify against a live
call (`e2e.sh`) when in doubt.

## Skills

- **`vocdoni-sdk`** — invoke when wiring client-side vote casting in `web/` (the `/vote` flow the
  backend deliberately skips), CSP/bundle auth, or reading election results via `@vocdoni/sdk`.
- **`vocdoni-ballot-protocol`** — invoke when reasoning about ballot encoding or the result matrix
  (choices ↔ vote arrays ↔ tally) for proposals/results.

Not applicable here: `vocdoni-go`/`go-*` (this is .NET, not Go), `davinci-sdk` (this targets the
SaaS API, not the Davinci protocol).
