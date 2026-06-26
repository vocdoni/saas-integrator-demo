# Vocdoni SaaS — Integrator Guide

> Build a product that lets **your customers run verifiable elections** — without touching a
> blockchain, a crypto library, or an election-results algorithm. This is the guide I wish I'd had
> before writing the reference implementation in this repository.

- **Base URL (dev):** `https://saas-api-dev.vocdoni.net`
- **Reference implementation:** this repo (`saas-api-demo`) — an HOA voting platform built exactly on
  the flow below.
- **Languages in this guide:** `curl` is the canonical spine; **Java**, **Python** and **C#** snippets
  accompany the Quickstart and the highest-value calls.

---

## 1. Overview

### Who this is for

You're building a **platform that offers election-running to your own customers**. You integrate
Vocdoni SaaS as white-labelled, multi-tenant infrastructure so *your users* can run verifiable,
censorship-resistant elections — you are **not** running one organization's elections, you're
reselling the capability to many.

You are the **integrator** (the middle layer). Each of your customers becomes a **managed
organization** that you provision and operate on their behalf with a single API key. Vocdoni supplies
the chain, the cryptography, and the infrastructure underneath.

This repo is the worked example: an **HOA voting platform** where the integrator creates **one managed
org per association**, and each association then runs its own proposals for its homeowners.

```
                 ┌─────────────────────────────────────────────────────┐
   YOU           │  Integrator organization  (one API key: vsk_…)      │
 (the platform)  └───────────────┬─────────────────────────────────────┘
                                 │ provisions & operates, path-less
            ┌────────────────────┼────────────────────┐
            ▼                    ▼                    ▼
     Managed org A        Managed org B        Managed org C      ← one per customer/tenant
     (Association 1)      (Association 2)      (Association 3)
            │
            │  per managed org, you run the election lifecycle:
            ▼
   Members → Group → Census → Process (election) → Votes → Results
                                   ▲                        ▲
                                   └──── Jobs (async) ──────┘
```

One integrator key fans out to many managed orgs. Everything below the managed-org line is the
**election lifecycle** you'll repeat for every vote your customers run.

### Three truths to internalize first

These three things trip up everyone (they tripped up this repo). Read them now and the rest of the
guide will make sense.

> **1. Heavy operations are asynchronous.** Publishing a process, changing its status, relaying a vote,
> and bulk-adding members don't finish in the HTTP call — they return a **`jobId`** and do the on-chain
> work on a background worker. You **poll `GET /jobs/{jobId}`** until `status` is `completed`.

> **2. On-chain ids and addresses are hex strings.** Organization addresses and process ids travel as
> hex strings like `0x4a3b…` (Vocdoni's `HexBytes` wire format). The swagger schema renders some of
> them as `array of integer` — ignore that; send and read **hex strings**.

> **3. Your API key *is* your identity.** The integrator organization is resolved from the key, so the
> integrator endpoints are **path-less** (`POST /integrator/organizations`, not
> `/organizations/{you}/managed`). You never put your own address in a URL.

### Two ways to integrate

| Surface | Use it for |
|---|---|
| **REST API** (this guide) | Everything server-side: provisioning orgs, members, censuses, processes, reading results. |
| **`@vocdoni/sdk`** (TypeScript) | The **voter's browser**: encoding a ballot and signing the vote transaction (client-side cryptography). The REST API *relays* an already-signed vote; it does not build one. |

This guide covers the REST API end-to-end. The one place you hand off to the SDK — actually casting a
ballot — is called out explicitly in [Voting process](#voting-process).

---

## 2. Quickstart

This runs the entire lifecycle once: create a managed org for a customer, add a voter, build an
auth-only census, open a yes/no election, and read the tally. It's the same sequence the repo's
`e2e.sh` and `create-process.sh` run green against the dev API.

### Prerequisites

1. A Vocdoni **integrator account** (free tier, via the SaaS dashboard).
2. An **API key** (`vsk_…`) minted under your integrator org, carrying the scopes `managed:write`,
   `managed:read`, and `quota:read`.
3. The dev base URL: `https://saas-api-dev.vocdoni.net`.

> The free tier allows **one managed organization**. Delete it (see [Organizations](#organizations)) or
> request more quota to run the Quickstart repeatedly.

### Set up a client

Every request carries `Authorization: Bearer <your-key>` and (for writes) `Content-Type:
application/json`. That's the whole authentication story — see [Authentication](#authentication).

**curl**
```bash
export VOCDONI_BASE_URL="https://saas-api-dev.vocdoni.net"
export VOCDONI_API_TOKEN="vsk_your_key_here"

auth=(-H "Authorization: Bearer $VOCDONI_API_TOKEN" -H "Content-Type: application/json")
```

**Java** (JDK 11+, `java.net.http`)
```java
import java.net.URI;
import java.net.http.*;

String base  = "https://saas-api-dev.vocdoni.net";
String token = System.getenv("VOCDONI_API_TOKEN");
HttpClient http = HttpClient.newHttpClient();

HttpRequest.Builder req(String path) {
    return HttpRequest.newBuilder(URI.create(base + path))
        .header("Authorization", "Bearer " + token)
        .header("Content-Type", "application/json");
}
```

**Python** (`pip install requests`)
```python
import os, requests

BASE  = "https://saas-api-dev.vocdoni.net"
TOKEN = os.environ["VOCDONI_API_TOKEN"]
s = requests.Session()
s.headers.update({"Authorization": f"Bearer {TOKEN}", "Content-Type": "application/json"})
```

**C#** (.NET, `System.Net.Http`)
```csharp
using System.Net.Http;
using System.Net.Http.Json;

var http = new HttpClient { BaseAddress = new Uri("https://saas-api-dev.vocdoni.net") };
http.DefaultRequestHeaders.Authorization =
    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("VOCDONI_API_TOKEN"));
```

### The end-to-end flow

Each step names the field you carry into the next. The voter-facing casting step is intentionally
omitted here (it's client-side — see [Voting process](#voting-process)); the Quickstart proves the
full server-side path up to reading results.

**curl** — a complete, runnable script
```bash
#!/usr/bin/env bash
set -euo pipefail
B="$VOCDONI_BASE_URL"
auth=(-H "Authorization: Bearer $VOCDONI_API_TOKEN" -H "Content-Type: application/json")

# 1. Create a managed org for your customer. The integrator is resolved from the key (path-less).
ORG=$(curl -s "${auth[@]}" -X POST "$B/integrator/organizations" \
  -d '{"type":"association","meta":{"name":"Maple Street HOA"}}' | jq -r .address)
echo "managed org: $ORG"

# 2. Add a member. Returns a jobId — bulk member writes are async.
JOB=$(curl -s "${auth[@]}" -X POST "$B/organizations/$ORG/members" \
  -d '{"members":[{"name":"Alice","memberNumber":"A-101","email":"alice@maple.local","weight":"1"}]}' \
  | jq -r .jobId)
# poll until added
until [ "$(curl -s "${auth[@]}" "$B/organizations/$ORG/members/job/$JOB" | jq -r '.progress')" = "100" ]; do sleep 1; done

# 3. Create an "all members" group (the bridge to publishing an auth-only census).
GROUP=$(curl -s "${auth[@]}" -X POST "$B/organizations/$ORG/groups" \
  -d '{"title":"All Homeowners","includeAllMembers":true}' | jq -r .id)

# 4. Create an auth-only census: voters authenticate by member number, no 2FA.
CENSUS=$(curl -s "${auth[@]}" -X POST "$B/census" \
  -d "{\"orgAddress\":\"$ORG\",\"authFields\":[\"memberNumber\"]}" | jq -r .id)

# 5. Publish the census THROUGH THE GROUP (auth-only requires group-publish, see Census).
curl -s "${auth[@]}" -X POST "$B/census/$CENSUS/group/$GROUP/publish" \
  -d '{"authFields":["memberNumber"],"weighted":false}' >/dev/null

# 6. Create a yes/no process.
PROCESS=$(curl -s "${auth[@]}" -X POST "$B/process" -d "{
  \"orgAddress\":\"$ORG\",\"censusId\":\"$CENSUS\",
  \"metadata\":{\"title\":\"Repaint the fence?\"},
  \"electionParams\":{
    \"title\":{\"default\":\"Repaint the fence?\"},
    \"description\":{\"default\":\"Annual maintenance vote\"},
    \"questions\":[{\"title\":{\"default\":\"Repaint the fence?\"},
      \"choices\":[{\"title\":{\"default\":\"Yes\"},\"value\":0},
                   {\"title\":{\"default\":\"No\"},\"value\":1}]}],
    \"voteType\":{\"maxCount\":1,\"maxValue\":1},
    \"electionType\":{\"autostart\":true,\"interruptible\":true},
    \"startDate\":\"2026-06-25T14:30:00Z\",\"endDate\":\"2026-07-02T14:30:00Z\",
    \"maxCensusSize\":1000
  }}" | jq -r .)   # POST /process returns the ProcessID as a bare JSON string

# 7. Publish on-chain (async); wait for the job to finish.
PJOB=$(curl -s "${auth[@]}" -X POST "$B/process/$PROCESS/publish" | jq -r .jobId)
until [ "$(curl -s "$B/jobs/$PJOB" | jq -r .status)" = "completed" ]; do sleep 2; done

# ... voters cast ballots client-side via @vocdoni/sdk ...

# 8. Read results (public, no auth needed) — addressed by the ProcessID.
curl -s "$B/process/$PROCESS/results" | jq
```

**Python** — the same flow
```python
import os, time, requests

B = "https://saas-api-dev.vocdoni.net"
s = requests.Session()
s.headers.update({"Authorization": f"Bearer {os.environ['VOCDONI_API_TOKEN']}",
                  "Content-Type": "application/json"})

def post(path, body=None): r = s.post(B+path, json=body); r.raise_for_status(); return r
def get(path):             r = s.get(B+path);             r.raise_for_status(); return r

# 1. managed org
org = post("/integrator/organizations",
           {"type": "association", "meta": {"name": "Maple Street HOA"}}).json()["address"]

# 2. member (async)
job = post(f"/organizations/{org}/members",
           {"members": [{"name": "Alice", "memberNumber": "A-101",
                         "email": "alice@maple.local", "weight": "1"}]}).json()["jobId"]
while get(f"/organizations/{org}/members/job/{job}").json()["progress"] < 100:
    time.sleep(1)

# 3. group
group = post(f"/organizations/{org}/groups",
             {"title": "All Homeowners", "includeAllMembers": True}).json()["id"]

# 4. census (auth-only)
census = post("/census", {"orgAddress": org, "authFields": ["memberNumber"]}).json()["id"]

# 5. group-publish
post(f"/census/{census}/group/{group}/publish",
     {"authFields": ["memberNumber"], "weighted": False})

# 6. create the process
process = post("/process", {
    "orgAddress": org, "censusId": census,
    "metadata": {"title": "Repaint the fence?"},
    "electionParams": {
        "title": {"default": "Repaint the fence?"},
        "description": {"default": "Annual maintenance vote"},
        "questions": [{"title": {"default": "Repaint the fence?"},
                       "choices": [{"title": {"default": "Yes"}, "value": 0},
                                   {"title": {"default": "No"}, "value": 1}]}],
        "voteType": {"maxCount": 1, "maxValue": 1},
        "electionType": {"autostart": True, "interruptible": True},
        "startDate": "2026-06-25T14:30:00Z", "endDate": "2026-07-02T14:30:00Z",
        "maxCensusSize": 1000,
    }}).json()           # bare JSON string

# 7. publish (async) → wait for the job
pjob = post(f"/process/{process}/publish").json()["jobId"]
while get(f"/jobs/{pjob}").json()["status"] != "completed":
    time.sleep(2)

# 8. results — addressed by the ProcessID
print(get(f"/process/{process}/results").json())
```

<details><summary><b>Java</b> — the same flow</summary>

```java
import java.net.URI;
import java.net.http.*;
import java.time.Duration;

// Minimal helpers; swap in Jackson/Gson for real JSON handling.
HttpClient http = HttpClient.newHttpClient();
String B = "https://saas-api-dev.vocdoni.net";
String token = System.getenv("VOCDONI_API_TOKEN");

java.util.function.BiFunction<String,String,HttpResponse<String>> post = (path, json) -> {
    try {
        var r = HttpRequest.newBuilder(URI.create(B + path))
            .header("Authorization", "Bearer " + token)
            .header("Content-Type", "application/json")
            .POST(json == null ? HttpRequest.BodyPublishers.noBody()
                               : HttpRequest.BodyPublishers.ofString(json)).build();
        return http.send(r, HttpResponse.BodyHandlers.ofString());
    } catch (Exception e) { throw new RuntimeException(e); }
};
java.util.function.Function<String,HttpResponse<String>> get = path -> {
    try {
        var r = HttpRequest.newBuilder(URI.create(B + path))
            .header("Authorization", "Bearer " + token).GET().build();
        return http.send(r, HttpResponse.BodyHandlers.ofString());
    } catch (Exception e) { throw new RuntimeException(e); }
};

// 1. managed org  → parse "address" from the response body
var org = post.apply("/integrator/organizations",
    "{\"type\":\"association\",\"meta\":{\"name\":\"Maple Street HOA\"}}").body();
// String orgAddr = jsonField(org, "address");

// 2. member (async) → "jobId"; poll /organizations/{addr}/members/job/{jobId} until progress == 100
// 3. group        → POST /organizations/{addr}/groups {"title":...,"includeAllMembers":true} → "id"
// 4. census       → POST /census {"orgAddress":...,"authFields":["memberNumber"]} → "id"
// 5. group-publish→ POST /census/{id}/group/{groupId}/publish {"authFields":["memberNumber"],"weighted":false}
// 6. process      → POST /process { ...electionParams... } → bare JSON string (the ProcessID)
// 7. publish      → POST /process/{process}/publish → "jobId"; poll /jobs/{jobId} until status=="completed"
// 8. results      → GET /process/{process}/results
```
The flow is identical to the curl/Python versions above; only the JSON (de)serialization differs. Use
Jackson or Gson to read the `address`, `jobId`, and `id` fields.
</details>

<details><summary><b>C#</b> — the same flow</summary>

```csharp
using System.Net.Http.Json;
using System.Text.Json;

var http = new HttpClient { BaseAddress = new Uri("https://saas-api-dev.vocdoni.net") };
http.DefaultRequestHeaders.Authorization =
    new("Bearer", Environment.GetEnvironmentVariable("VOCDONI_API_TOKEN"));

async Task<JsonElement> Post(string path, object? body) =>
    await (await http.PostAsJsonAsync(path, body)).Content.ReadFromJsonAsync<JsonElement>();
async Task<JsonElement> Get(string path) =>
    await http.GetFromJsonAsync<JsonElement>(path);

// 1. managed org
var org = (await Post("/integrator/organizations",
    new { type = "association", meta = new { name = "Maple Street HOA" } })).GetProperty("address").GetString();

// 2. member (async)
var job = (await Post($"/organizations/{org}/members",
    new { members = new[] { new { name = "Alice", memberNumber = "A-101",
                                  email = "alice@maple.local", weight = "1" } } })).GetProperty("jobId").GetString();
while ((await Get($"/organizations/{org}/members/job/{job}")).GetProperty("progress").GetInt32() < 100)
    await Task.Delay(1000);

// 3. group
var group = (await Post($"/organizations/{org}/groups",
    new { title = "All Homeowners", includeAllMembers = true })).GetProperty("id").GetString();

// 4. census  5. group-publish
var census = (await Post("/census", new { orgAddress = org, authFields = new[] { "memberNumber" } })).GetProperty("id").GetString();
await Post($"/census/{census}/group/{group}/publish", new { authFields = new[] { "memberNumber" }, weighted = false });

// 6. create the process
var process = (await Post("/process", new {
    orgAddress = org, censusId = census,
    metadata = new { title = "Repaint the fence?" },
    electionParams = new {
        title = new { @default = "Repaint the fence?" },
        description = new { @default = "Annual maintenance vote" },
        questions = new[] { new { title = new { @default = "Repaint the fence?" },
            choices = new[] { new { title = new { @default = "Yes" }, value = 0 },
                              new { title = new { @default = "No" },  value = 1 } } } },
        voteType = new { maxCount = 1, maxValue = 1 },
        electionType = new { autostart = true, interruptible = true },
        startDate = "2026-06-25T14:30:00Z", endDate = "2026-07-02T14:30:00Z",
        maxCensusSize = 1000,
    }})).GetString();   // bare JSON string (the ProcessID)

// 7. publish (async) → wait for the job
var pjob = (await Post($"/process/{process}/publish", null)).GetProperty("jobId").GetString();
JsonElement j;
do { await Task.Delay(2000); j = await Get($"/jobs/{pjob}"); }
while (j.GetProperty("status").GetString() != "completed");

// 8. results — addressed by the ProcessID
Console.WriteLine(await Get($"/process/{process}/results"));
```
</details>

That's the whole platform in miniature. The rest of the guide explains each concept, its options, and
its sharp edges.

---

## 3. Authentication

### API keys

Every call authenticates with a Bearer **API key** (`vsk_…`) minted under your integrator
organization:

```
Authorization: Bearer vsk_cc9147aa44407bcb8caacd9ae9fa181ed13dcb026cc3d1376edfc7f38aa21f9f
```

The key **is** your integrator identity. The server resolves your integrator org from the key, which
is why the integrator endpoints are **path-less** — you never pass your own org address in a URL.
(Under the hood the key is bound to its creating user and that user's org; the per-route scope check
and role check run on that resolved org.)

### Scopes

A key only works on an endpoint if it carries the right scope. Mint keys with exactly what your
integration needs:

| Scope | Grants |
|---|---|
| `managed:write` | Create and delete managed organizations (`POST`/`DELETE /integrator/organizations…`). |
| `managed:read` | List managed organizations (`GET /integrator/organizations`). |
| `quota:read` | Read integrator quota and usage (`GET /integrator`). |

The member/census/process endpoints under a managed org are authorized by your key acting as an admin
of that org (it created it), so no extra scope is needed for the election lifecycle itself.

> API keys can only be created **under integrator organizations**. A regular org can't mint one.

### The alternative: user sessions (JWT)

The same endpoints also accept a **JWT** from a user login (`POST /auth/login` → `{token}`), where the
integrator org is resolved from the user's session instead of a key. Integrators almost always use a
**key** for server-to-server automation; the JWT path exists for dashboards and interactive tools. The
two are interchangeable as far as the integrator endpoints are concerned.

### Failure modes

| Status | Meaning | Fix |
|---|---|---|
| `401 Unauthorized` | Missing/invalid key or JWT. | Check the `Authorization` header and key value. |
| `403` insufficient scope | Key lacks the endpoint's scope. | Re-mint the key with `managed:write` / `managed:read` / `quota:read`. |
| `403` not an integrator | The resolved org isn't an integrator. | Use a key minted under your integrator org. |

---

## 4. Core concepts

The lifecycle, one concept at a time. Each section follows the same shape: **what it is → the calls →
the field you carry forward → the gotchas.**

### Organizations

A **managed organization** is one customer/tenant on your platform. You create one per customer and
operate it with your integrator key. (In this repo, one managed org = one HOA association.)

**Create** — path-less; the integrator is your key's org.
```bash
curl "${auth[@]}" -X POST "$B/integrator/organizations" \
  -d '{"type":"association","meta":{"name":"Maple Street HOA"}}'
```
```jsonc
// 200 OK  → carry forward: address (hex string)
{ "address": "0x4a3b…", "type": "association", "meta": { "name": "Maple Street HOA" } }
```
The on-chain account is provisioned eagerly. The optional `ownerEmail` assigns an existing user as the
managed org's admin (defaults to your key's user).

**List** — paginated.
```bash
curl "${auth[@]}" "$B/integrator/organizations?page=1&limit=10"
```
```jsonc
{ "organizations": [ { "address": "0x4a3b…", "meta": { "name": "Maple Street HOA" } } ],
  "pagination": { "page": 1, "limit": 10, "total": 1 } }
```

**Quota & usage** — how many managed orgs/processes/census-seats you've used vs your limits.
```bash
curl "${auth[@]}" "$B/integrator"
```
```jsonc
{ "enabled": true,
  "limits": { "maxManagedOrgs": 1, "maxManagedProcesses": 0, "maxManagedCensusSize": 0 },
  "usage":  { "managedOrgs": 1, "managedProcesses": 3, "managedCensusSize": 42 } }
```
A `0` limit means **unlimited**.

**Delete** — cascade. Removes the managed org and all its DB-side data (members, censuses, processes,
bundles, CSP tokens, jobs, invites) and rolls back your usage counters.
```bash
curl "${auth[@]}" -X DELETE "$B/integrator/organizations/0x4a3b…"
```
```jsonc
{ "address": "0x4a3b…" }   // 200 OK
```

> **409 if elections are still active.** Deletion is blocked while any of the org's published
> elections is `READY` or `PAUSED` on-chain — end them first (`PUT /process/{id}/status` →
> `ended`). A `404` means the org is already gone (safe to treat as success). On-chain accounts and
> published elections are immutable on the Vochain and are *not* removed — only the off-chain data is.

**Gotchas**
- Addresses are **hex strings**, everywhere.
- **Free tier = 1 managed org.** Delete or get more quota to provision more customers.

### Members and groups

**Members** are a managed org's people (your customer's voters). **Groups** are named subsets of
members — and, crucially, a group is the **bridge that lets you publish an auth-only census** (next
section).

**Add members** — bulk, and **asynchronous** (returns a `jobId`).
```bash
curl "${auth[@]}" -X POST "$B/organizations/$ORG/members" -d '{
  "members": [
    { "name": "Alice", "surname": "Doe", "email": "alice@maple.local",
      "memberNumber": "A-101", "weight": "1" }
  ]
}'
```
```jsonc
{ "jobId": "a1b2c3…" }
```
Member fields: `name`, `surname`, `email`, `phone`, `memberNumber`, `nationalId`, `birthDate`,
`weight` (a **string**, e.g. `"1"`; weighted censuses use it as vote weight).

**Poll the add-members job** until it's done:
```bash
curl "${auth[@]}" "$B/organizations/$ORG/members/job/$JOB"
```
```jsonc
{ "added": 1, "total": 1, "progress": 100, "errors": [] }   // progress == 100 → done
```

<details><summary>Add + poll in <b>Python</b> / <b>C#</b></summary>

```python
job = post(f"/organizations/{org}/members",
           {"members": [{"name": "Alice", "memberNumber": "A-101", "weight": "1"}]}).json()["jobId"]
while True:
    st = get(f"/organizations/{org}/members/job/{job}").json()
    if st["progress"] >= 100:
        if st["errors"]: raise RuntimeError(st["errors"])
        break
    time.sleep(1)
```
```csharp
var job = (await Post($"/organizations/{org}/members",
    new { members = new[] { new { name = "Alice", memberNumber = "A-101", weight = "1" } } }))
    .GetProperty("jobId").GetString();
while ((await Get($"/organizations/{org}/members/job/{job}")).GetProperty("progress").GetInt32() < 100)
    await Task.Delay(1000);
```
</details>

**List members**
```bash
curl "${auth[@]}" "$B/organizations/$ORG/members"
```

**Delete members** — note the **plural** path and a body of ids.
```bash
curl "${auth[@]}" -X DELETE "$B/organizations/$ORG/members" -d '{"ids":["<memberId>"]}'
```

**Create a group** — an all-members group is the common case.
```bash
curl "${auth[@]}" -X POST "$B/organizations/$ORG/groups" \
  -d '{"title":"All Homeowners","includeAllMembers":true}'
```
```jsonc
{ "id": "665f…" }   // carry forward: group id
```

**Gotchas**
- Adding members is a **job** — don't build the census until `progress` is `100`.
- Delete is `DELETE /organizations/{addr}/members` (**plural**). The singular `/member` 404s.
- For an **auth-only** census, each `memberNumber` must be **unique** (it becomes the voting
  credential — see below).

### Census

A **census** is the eligible-voter list for an election, anchored to a cryptographic root. You choose
*how voters prove who they are* when you create it:

- `authFields` — fields a voter must present to authenticate (e.g. `memberNumber`). With only
  `authFields` set, the census is **auth-only** (no second factor).
- `twoFaFields` — fields used for a one-time-code second factor: `email` or `phone`.

This yields four census types: **`auth`** (auth-only), **`mail`**, **`sms`**, **`sms_or_mail`**.

**Create**
```bash
curl "${auth[@]}" -X POST "$B/census" \
  -d "{\"orgAddress\":\"$ORG\",\"authFields\":[\"memberNumber\"]}"
```
```jsonc
{ "id": "6a1f…" }   // carry forward: census id
```
`authFields` options: `name`, `surname`, `memberNumber`, `nationalId`, `birthDate`.
`twoFaFields` options: `email`, `phone`. (Add `"twoFaFields":["email"]` for an email-OTP census.)

**Publish** — this is where the **#1 gotcha** lives.

- For the **2FA types** (`mail` / `sms` / `sms_or_mail`), publish directly:
  ```bash
  curl "${auth[@]}" -X POST "$B/census/$CENSUS/publish"
  ```
- For **auth-only**, the plain publish **rejects** the census with `census type not found`. You must
  publish **through a group**, which both supports auth-only and populates participants from the group:
  ```bash
  curl "${auth[@]}" -X POST "$B/census/$CENSUS/group/$GROUP/publish" \
    -d '{"authFields":["memberNumber"],"weighted":false}'
  ```

Either way you get the published census:
```jsonc
{ "root": "deadbeef…", "size": 1, "uri": "https://…" }
```
Set `"weighted": true` to make each member's `weight` count as vote weight.

**Inspect participants**
```bash
curl "${auth[@]}" "$B/census/$CENSUS/participants"
```
```jsonc
{ "censusId": "6a1f…", "memberIds": ["…"] }
```

**Gotchas**
- **Auth-only must be published via a group** (`/census/{id}/group/{groupid}/publish`). This is the
  single most common stumble.
- Auth-only credential is derived from the auth field, so **`memberNumber` must be unique** across the
  census — duplicates fail at publish.
- One published census can back **multiple** processes (reuse it across votes).

### Voting process

A **process** is an election. You create it (off-chain and fully editable at first), then
**publish** it on-chain (async). Voters then cast ballots; you read results. One **ProcessID**
identifies it throughout — `POST /process` returns it and you reuse it for publish, status, and results.

**Create the process**
```bash
curl "${auth[@]}" -X POST "$B/process" -d "{
  \"orgAddress\": \"$ORG\",
  \"censusId\": \"$CENSUS\",
  \"metadata\": { \"title\": \"Repaint the fence?\" },
  \"electionParams\": {
    \"title\":       { \"default\": \"Repaint the fence?\" },
    \"description\": { \"default\": \"Annual maintenance vote\" },
    \"questions\": [
      { \"title\": { \"default\": \"Repaint the fence?\" },
        \"choices\": [
          { \"title\": { \"default\": \"Yes\" }, \"value\": 0 },
          { \"title\": { \"default\": \"No\"  }, \"value\": 1 }
        ] }
    ],
    \"voteType\":     { \"maxCount\": 1, \"maxValue\": 1 },
    \"electionType\": { \"autostart\": true, \"interruptible\": true },
    \"startDate\": \"2026-06-25T14:30:00Z\",
    \"endDate\":   \"2026-07-02T14:30:00Z\",
    \"maxCensusSize\": 1000
  }
}"
```
```text
"665f0c…"   ← POST /process returns the ProcessID as a BARE JSON string
```

Field notes:
- Titles/descriptions are **multilingual maps**: `{ "default": "…", "es": "…" }`.
- Each choice has a numeric `value`; results are reported per choice (see [Results](#results--jobs)).
- `voteType.maxCount` = how many selections a voter makes; `voteType.maxValue` = the max value per
  selection. For a single yes/no question both are `1`. For richer ballots (approval, ranked,
  quadratic, multi-question), see the Vocdoni **ballot protocol** docs — the numbers have precise
  meaning there.
- `electionType.autostart` opens the vote at `startDate`; `interruptible` lets you pause/end it.

**Publish on-chain (async)**
```bash
curl "${auth[@]}" -X POST "$B/process/$PROCESS/publish"
```
```jsonc
{ "jobId": "a1b2c3…" }   // 202 Accepted — the on-chain work happens on a worker
```
Poll the job until it completes. Its `result.address` is the **on-chain election id** — an internal
value you can ignore here; it surfaces again only when wiring the voter signing flow below:
```bash
curl "$B/jobs/$JOBID"
```
```jsonc
{ "jobId": "a1b2c3…", "type": "publish_process", "status": "completed",
  "result": { "address": "0x9f2c…", "status": "READY" } }
```
Publishing is **idempotent**: if the process is already published, you get `200` with
`{ "address", "status" }` directly instead of a new job.

> **Tip:** keep using the **ProcessID** (`$PROCESS`, the 24-hex id `POST /process` returned) for
> status, results, metadata, and the bundle — the same id, before and after publishing. The on-chain
> election id in `result.address` only surfaces client-side, when the voter signs their ballot.

**Change status** — also async (`ready`, `paused`, `ended`, `canceled`). Uses the **ProcessID**.
```bash
curl "${auth[@]}" -X PUT "$B/process/$PROCESS/status" -d '{"status":"ended"}'
```
```jsonc
{ "jobId": "d4e5f6…" }   // 202 — poll /jobs/{jobId}
```

#### Casting a vote (the voter's side)

Voting is **voter-facing and cryptographic**, and it's the one place you hand off to the
`@vocdoni/sdk` in the voter's browser. The server side you provide is a **process bundle** plus the
CSP (Credential Service Provider) endpoints; the SDK does the ballot encoding and transaction signing.

1. **Bundle the process** (server-side, with your key). A bundle is the voter-facing entry point and
   ties the process(es) to the census. Reference each process by its **ProcessID** — since saas-backend
   #554 the bundle resolves it to the on-chain id for you (passing the on-chain id still works too).
   ```bash
   curl "${auth[@]}" -X POST "$B/process/bundle" \
     -d "{\"censusId\":\"$CENSUS\",\"processes\":[\"$PROCESS\"]}"
   ```
   ```jsonc
   { "root": "deadbeef…", "uri": "https://…/process/bundle/<bundleId>" }
   ```
   The **bundleId** is the last path segment of `uri`.

2. **Voter authenticates** against the bundle (two steps). Step `0` identifies the voter and triggers
   a challenge; step `1` submits the one-time code. For an **auth-only** census, step `0` returns a
   pre-verified token and there's no code to enter.
   ```bash
   # step 0 — identify (member number, and email/phone for 2FA censuses)
   curl -X POST "$B/process/bundle/$BUNDLE/auth/0" -H "Content-Type: application/json" \
     -d '{"participantId":"A-101","email":"alice@maple.local"}'
   # step 1 — submit the OTP (2FA censuses only)
   curl -X POST "$B/process/bundle/$BUNDLE/auth/1" -H "Content-Type: application/json" \
     -d '{"authToken":"<token>","code":"123456"}'
   ```
   ```jsonc
   { "authToken": "deadbeef…", "signature": "…", "weight": "1" }
   ```
   (`POST /process/bundle/{id}/auth/resend` re-sends a challenge if it expires.)

3. **CSP signs the voter's ballot.** With a verified `authToken`, the CSP blind/ECDSA-signs the
   voter's address for the chosen election — the `@vocdoni/sdk` orchestrates this and the ballot
   encoding for you:
   ```bash
   curl -X POST "$B/process/bundle/$BUNDLE/sign" -H "Content-Type: application/json" \
     -d '{"authToken":"deadbeef…","electionId":"0x9f2c…","payload":"<addr>","tokenR":"<R>"}'
   ```
   Each token can sign each process **once** (no double-voting).

4. **Relay the signed vote** to the chain (public, async). The SDK produces the signed transaction
   payload; you (or the SDK) relay it. The target process is read from the signed envelope, so the
   path carries no id:
   ```bash
   curl -X POST "$B/vote" -H "Content-Type: application/json" \
     -d '{"txPayload":"<hex of the signed vote tx>"}'
   ```
   ```jsonc
   { "jobId": "…" }   // 202 — poll /jobs/{jobId}; result.voteID is the vote nullifier
   ```

> **What the REST API does and doesn't do:** it *authenticates* the voter and *relays* an
> already-signed vote. It does **not** build or sign the ballot — that's client-side cryptography in
> `@vocdoni/sdk`. This repo's web app displays the process and defers casting to the SDK.

**Gotchas**
- `POST /process` returns a **bare string** (the ProcessID), not an object.
- Publish and status changes are **jobs** — read the outcome from `/jobs/{jobId}`, not the POST body.
- Address the process by its **ProcessID** for status, results, metadata, and the bundle. The on-chain
  election id (`result.address`) is needed only client-side, to sign voter payloads.

### Results & Jobs

#### Jobs — the async spine

Anything that touches the chain returns a **`jobId`**; you poll one endpoint to learn the outcome.

```bash
curl "$B/jobs/$JOBID"     # public — the 32-byte job id is the capability
```
```jsonc
{ "jobId": "a1b2c3…",
  "type": "publish_process",          // org_members | census_participants | publish_process |
                                      //   set_process_status | relay_vote
  "status": "completed",              // pending | completed | failed
  "result": { "address": "0x9f2c…",   // on publish: the on-chain election id (voting flow only)
              "status": "READY",      // on status change: the new status
              "voteID": "" },         // on relay_vote: the vote nullifier
  "error": "" }                       // populated only when status == failed
```

Rules of thumb:
- Always `200`, even for failures — branch on the **`status`** field.
- `completed` → read `result`; `failed` → read `error`; otherwise keep polling (2s is plenty).
- Bulk member adds use a richer **members-job** shape instead:
  `GET /organizations/{addr}/members/job/{jobid}` → `{ added, total, progress, errors }`.

#### Results

Public, no auth. Available while voting is live (running tally) and after it ends (final).
```bash
curl "$B/process/$PROCESS/results"
```
```jsonc
{ "status": "RESULTS_AVAILABLE",
  "finalResults": true,
  "voteCount": 42,
  "startDate": "2026-06-25T14:30:00Z",
  "endDate":   "2026-07-02T14:30:00Z",
  "results": [ ["25", "17"] ] }
```

**Reading the `results` matrix.** It's `results[question][choice]` — one inner array per question, one
number per choice (as **strings**, because tallies can be big/weighted). For our single yes/no
question with choices `Yes(value 0)` and `No(value 1)`:

```
results[0] = ["25", "17"]
              └Yes  └No        → 25 voted Yes, 17 voted No   (voteCount = 42)
```

Multi-question elections add more inner arrays (`results[0]`, `results[1]`, …). This **discrete**
aggregation (count per choice) is the common case; richer ballots (ranked, quadratic, budget) use
**index-weighted** aggregation where the numbers mean something different — the Vocdoni **ballot
protocol** docs explain how each variant maps a numeric ballot to this matrix.

- `finalResults: false` → the election is still open; the tally is provisional.
- `finalResults: true`  → the election has ended; results are final.

---

## 5. Gotchas recap — what I wish I'd known

A one-screen field guide to the sharp edges, all of which this repo hit:

1. **Async everywhere.** Publish, status change, vote relay, and bulk member adds return a `jobId` —
   poll `GET /jobs/{jobId}`. The POST body is *not* the result.
2. **Hex strings, not int arrays.** Org addresses and process ids are hex strings on the wire, despite
   what some swagger schemas render.
3. **Integrator endpoints are path-less.** `POST /integrator/organizations`, resolved from your key.
   (The old `/organizations/{address}/managed` paths are gone.)
4. **Auth-only censuses must be published via a group** (`/census/{id}/group/{groupid}/publish`); the
   plain `/publish` rejects them.
5. **Unique member numbers** for auth-only — `memberNumber` is the voting credential.
6. **Delete members is the plural path** `DELETE /organizations/{addr}/members` with a body of `ids`.
7. **Free tier = 1 managed org.** Deleting one frees the slot (and the cascade reclaims quota), but is
   blocked with `409` while elections are still active.
8. **One ProcessID throughout.** Use the ProcessID (from `POST /process`) for status, results,
   metadata, and the bundle — before and after publish. The on-chain election id (`result.address`)
   is only used client-side, to sign voter payloads.
9. **The API relays votes; it doesn't build them.** Ballot encoding and signing are client-side in
   `@vocdoni/sdk`. Relay is path-less: `POST /vote` (the signed envelope names the process).
10. **Read the results matrix as `results[question][choice]`**, values are strings.

## Where to go next

- **`@vocdoni/sdk`** — the TypeScript SDK for the voter side (ballot encoding, anonymous/ZK voting,
  vote signing). The missing half of the casting flow above.
- **Ballot protocol** — how numeric ballots (approval, ranked, quadratic, budget, multi-question) map
  to the `voteType` fields and the results matrix.
- **API reference** — the full swagger for every endpoint and field.
- **This repository** (`saas-api-demo`) — a complete, runnable reference implementation of everything
  above: see `e2e.sh` and `create-process.sh` for the live calls, and `src/HoaVoting.Api/Services/Vocdoni/`
  for a typed client.
