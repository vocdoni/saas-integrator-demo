# Plan: React web app for the Homeowners Voting Platform backend

## Context

The Homeowners Voting Platform backend (ASP.NET Core .NET 10, Dockerized at `:5095`, JWT auth) is complete and
tested. It needs a **web UI** for two roles that already exist in the API:

- **Backend admin** (`SuperAdmin`) — create & list homeowners' associations.
- **Association admin** (`Owner`) — manage their association's **memberbase** (homeowners) and its
  **voting processes** (proposals: create, view results, close).

Decisions (confirmed): **React SPA (Vite)**, memberbase loadable **manually + via CSV upload**.

Deployment approach (lazy): the React build is bundled into the backend's `wwwroot` and served by
the same .NET app — one container, one port (`:5095`), **same-origin (no CORS, no nginx)**. Local
dev uses Vite's dev server with a `/api` proxy. No new compose service.

## Backend changes (small)

1. **`src/HoaVoting.Api/Controllers/AssociationsController.cs`** — make `GET /api/associations`
   role-aware so an Owner can discover their association. Change `[Authorize(Roles = SuperAdmin)]`
   → `[Authorize]` and filter: `SuperAdmin` → all; `Owner` → `where a.OwnerUserId == CurrentUserId`.
   (Reuse `CurrentRole`/`CurrentUserId` from `ApiControllerBase`.)
2. **`src/HoaVoting.Api/Dtos/Dtos.cs`** + **`AuthController.cs`** — add `Role` to `LoginResponse`
   so the SPA routes by role without decoding the JWT. Return `user.Role.ToString()`.
3. **`src/HoaVoting.Api/Program.cs`** — serve the SPA: `app.UseDefaultFiles()` +
   `app.UseStaticFiles()` (before `MapControllers`) and `app.MapFallbackToFile("index.html")`
   (after) so refreshes resolve to the app. No CORS needed.
4. **`Dockerfile`** — add a `node:22` build stage that runs `npm ci && npm run build` in `web/`,
   then `COPY --from=web /web/dist ./wwwroot` into the runtime image. (Backend-only `dotnet run`
   still works; `wwwroot` is just empty without a build.)

These are additive — `e2e.sh` (admin token) and the unit tests are unaffected.

## Frontend — `web/` (new Vite React app)

Dependencies kept minimal: `react`, `react-dom`, `vite`, `@vitejs/plugin-react`. **No** router, UI
kit, or state lib — role-based conditional rendering + a little local state is enough. Hand-rolled
CSS. CSV parsed with plain JS (no papaparse). // ponytail: no deps for what the platform/stdlib does.

```
web/
  package.json
  vite.config.js          # server.proxy: '/api' -> http://localhost:5095 (dev only)
  index.html
  src/
    main.jsx
    api.js                # fetch wrapper: base '/api', attaches Bearer from localStorage, throws on !ok
    auth.js               # login()/logout()/getToken()/getRole() over localStorage
    App.jsx               # no token -> <Login>; role SuperAdmin -> <AdminPage>; Owner -> <OwnerPage>
    components/
      Login.jsx           # email+password -> POST /api/auth/login -> store {token, role}
      AdminPage.jsx       # table of associations + create form (name, ownerEmail, ownerPassword)
      OwnerPage.jsx       # GET /api/associations -> [0]; renders Memberbase + Proposals; logout
      Memberbase.jsx      # list/add/delete homeowners + CSV upload
      Proposals.jsx       # list/create/results/close
    styles.css
```

### Views & API wiring (all endpoints already exist)
- **Login** → `POST /api/auth/login` → `{token, role}` in localStorage.
- **Admin** (`AdminPage`):
  - list: `GET /api/associations`
  - create: `POST /api/associations` `{name, ownerEmail, ownerPassword}`
- **Owner** (`OwnerPage` → resolves association via `GET /api/associations` `[0]`):
  - **Memberbase** (`Memberbase.jsx`):
    - list `GET /api/associations/{id}/homeowners`
    - add `POST .../homeowners` `{name, memberNumber, email?}`
    - delete `DELETE .../homeowners/{memberId}`
    - CSV upload: parse `First Name,Member Number` (optional `Email`) and POST each row
      (mirror `e2e.sh` loader; note auth-only needs **unique member numbers**)
  - **Proposals** (`Proposals.jsx`):
    - list `GET .../proposals`
    - create `POST .../proposals` `{title, description, choices[{title}], startDate, endDate,
      allowMultiple, twoFactorAuth}` (default `twoFactorAuth=false` = CSP by member number; the
      create call blocks ~10-30s on async publish — show a pending state)
    - results `GET .../proposals/{pid}/results` (render `voteCount`, status, tally matrix)
    - close `POST .../proposals/{pid}/close`

`api.js` surfaces non-2xx (incl. the backend's `502 vocdoni_upstream`) so the UI shows real errors.

## Verification

- **Dev:** backend up (`docker compose up -d` or `dotnet run`), then `cd web && npm install &&
  npm run dev` → open `http://localhost:5173`.
  - Login as admin (`.env` `ADMIN_EMAIL`/`ADMIN_PASSWORD`) → create an association.
  - Login as that owner → add homeowners (manual + CSV `memberbase-test.csv`) → create a proposal
    (no-2FA) → see it reach `READY`/results → close it.
- **Container (single image):** `docker compose up --build` → open `http://localhost:5095` → same
  flow end-to-end (SPA + API same origin).
- Re-run `./e2e.sh` and `dotnet test` to confirm backend changes didn't regress.
