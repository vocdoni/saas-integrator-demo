# Quickstart

This runs the entire lifecycle once: create a managed organization for a customer, add a voter, build
an auth-only census, open a yes/no election, publish it on-chain, and read the tally. Any HTTP client
works the same way — the examples below are curl, C#, and Python.

The one step omitted here is **casting a ballot**: that's voter-facing client-side cryptography, done
in the browser by the SDK. The Quickstart proves the full server-side path up to reading results; see
[Voting processes → Casting a vote](../core-concepts/voting-processes.md#casting-a-vote) for the rest.

## Before you start

1. A Vocdoni **integrator account** (free tier, via the SaaS dashboard).
2. An **API key** minted under your integrator organization, carrying the `managed:write`,
   `managed:read`, and `quota:read` scopes. See [API keys](../managed-platform/api-keys.md).
3. A **base URL**: `https://saas-api.vocdoni.net` (production), or `https://saas-api-dev.vocdoni.net` /
   `https://saas-api-stg.vocdoni.net` for dev/staging.

> The free tier allows **one managed organization**. Delete it (see
> [Managed organizations](../managed-platform/managed-organizations.md)) or request more quota to run
> the Quickstart repeatedly.

## Set up a client

Every request carries `Authorization: Bearer <your-api-key>` and, for writes,
`Content-Type: application/json`. That is the whole authentication story — the key *is* your
integrator identity, and the server resolves your integrator organization from it (which is why the
integrator endpoints take no address in the path).

```bash
export VOCDONI_BASE_URL="https://saas-api.vocdoni.net"
export VOCDONI_API_TOKEN="vsk_your_key_here"
auth=(-H "Authorization: Bearer $VOCDONI_API_TOKEN" -H "Content-Type: application/json")
B="$VOCDONI_BASE_URL"
```

<details><summary><b>C#</b> (.NET, <code>System.Net.Http</code>)</summary>

```csharp
using System.Net.Http.Json;
using System.Text.Json;

var http = new HttpClient { BaseAddress = new Uri("https://saas-api.vocdoni.net") };
http.DefaultRequestHeaders.Authorization =
    new("Bearer", Environment.GetEnvironmentVariable("VOCDONI_API_TOKEN"));

async Task<JsonElement> Post(string path, object? body) =>
    await (await http.PostAsJsonAsync(path, body)).Content.ReadFromJsonAsync<JsonElement>();
async Task<JsonElement> Get(string path) => await http.GetFromJsonAsync<JsonElement>(path);
```
</details>

<details><summary><b>Python</b> (<code>pip install requests</code>)</summary>

```python
import os, time, requests

B = "https://saas-api.vocdoni.net"
s = requests.Session()
s.headers.update({"Authorization": f"Bearer {os.environ['VOCDONI_API_TOKEN']}",
                  "Content-Type": "application/json"})

def post(path, body=None): r = s.post(B + path, json=body); r.raise_for_status(); return r
def get(path):             r = s.get(B + path);             r.raise_for_status(); return r
```
</details>

## The end-to-end flow

Each step names the field you carry into the next.

```bash
#!/usr/bin/env bash
set -euo pipefail

# 1. Create a managed org for your customer. The integrator is resolved from the key (path-less).
ORG=$(curl -s "${auth[@]}" -X POST "$B/integrator/organizations" \
  -d '{"type":"association","meta":{"name":"Maple Street HOA"}}' | jq -r .address)

# 2. Add a member. Returns a jobId — bulk member writes are async.
JOB=$(curl -s "${auth[@]}" -X POST "$B/organizations/$ORG/members" \
  -d '{"members":[{"name":"Alice","memberNumber":"A-101","email":"alice@example.org","weight":"1"}]}' \
  | jq -r .jobId)
until [ "$(curl -s "${auth[@]}" "$B/organizations/$ORG/members/job/$JOB" | jq -r .progress)" = "100" ]; do sleep 1; done

# 3. Create an "all members" group (the bridge to publishing an auth-only census).
GROUP=$(curl -s "${auth[@]}" -X POST "$B/organizations/$ORG/groups" \
  -d '{"title":"All voters","includeAllMembers":true}' | jq -r .id)

# 4. Create an auth-only census: voters authenticate by member number, no 2FA.
CENSUS=$(curl -s "${auth[@]}" -X POST "$B/census" \
  -d "{\"orgAddress\":\"$ORG\",\"authFields\":[\"memberNumber\"]}" | jq -r .id)

# 5. Publish the census THROUGH THE GROUP (auth-only requires group-publish).
curl -s "${auth[@]}" -X POST "$B/census/$CENSUS/group/$GROUP/publish" \
  -d '{"authFields":["memberNumber"],"weighted":false}' >/dev/null

# 6. Create a yes/no process. POST /process returns the ProcessID as a bare JSON string.
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
    \"startDate\":\"2026-07-01T09:00:00Z\",\"endDate\":\"2026-07-08T09:00:00Z\",
    \"maxCensusSize\":1000
  }}" | jq -r .)

# 7. Publish on-chain (async); wait for the job to finish.
PJOB=$(curl -s "${auth[@]}" -X POST "$B/process/$PROCESS/publish" | jq -r .jobId)
until [ "$(curl -s "$B/jobs/$PJOB" | jq -r .status)" = "completed" ]; do sleep 2; done

# ... voters cast ballots client-side with the SDK ...

# 8. Read results (public, no auth needed) — addressed by the ProcessID.
curl -s "$B/process/$PROCESS/results" | jq
```

<details><summary><b>C#</b> — the same flow</summary>

```csharp
// 1. managed org
var org = (await Post("/integrator/organizations",
    new { type = "association", meta = new { name = "Maple Street HOA" } })).GetProperty("address").GetString();

// 2. member (async) → poll the members-job until progress == 100
var job = (await Post($"/organizations/{org}/members",
    new { members = new[] { new { name = "Alice", memberNumber = "A-101",
                                  email = "alice@example.org", weight = "1" } } })).GetProperty("jobId").GetString();
while ((await Get($"/organizations/{org}/members/job/{job}")).GetProperty("progress").GetInt32() < 100)
    await Task.Delay(1000);

// 3. group   4. census (auth-only)   5. group-publish
var group = (await Post($"/organizations/{org}/groups",
    new { title = "All voters", includeAllMembers = true })).GetProperty("id").GetString();
var census = (await Post("/census", new { orgAddress = org, authFields = new[] { "memberNumber" } })).GetProperty("id").GetString();
await Post($"/census/{census}/group/{group}/publish", new { authFields = new[] { "memberNumber" }, weighted = false });

// 6. create the process → bare JSON string (the ProcessID)
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
        startDate = "2026-07-01T09:00:00Z", endDate = "2026-07-08T09:00:00Z",
        maxCensusSize = 1000,
    }})).GetString();

// 7. publish (async) → wait for the job
var pjob = (await Post($"/process/{process}/publish", null)).GetProperty("jobId").GetString();
JsonElement j;
do { await Task.Delay(2000); j = await Get($"/jobs/{pjob}"); }
while (j.GetProperty("status").GetString() != "completed");

// 8. results — addressed by the ProcessID
Console.WriteLine(await Get($"/process/{process}/results"));
```
</details>

<details><summary><b>Python</b> — the same flow</summary>

```python
# 1. managed org
org = post("/integrator/organizations",
           {"type": "association", "meta": {"name": "Maple Street HOA"}}).json()["address"]

# 2. member (async) → poll the members-job
job = post(f"/organizations/{org}/members",
           {"members": [{"name": "Alice", "memberNumber": "A-101",
                         "email": "alice@example.org", "weight": "1"}]}).json()["jobId"]
while get(f"/organizations/{org}/members/job/{job}").json()["progress"] < 100:
    time.sleep(1)

# 3. group   4. census (auth-only)   5. group-publish
group = post(f"/organizations/{org}/groups",
             {"title": "All voters", "includeAllMembers": True}).json()["id"]
census = post("/census", {"orgAddress": org, "authFields": ["memberNumber"]}).json()["id"]
post(f"/census/{census}/group/{group}/publish", {"authFields": ["memberNumber"], "weighted": False})

# 6. create the process → bare JSON string (the ProcessID)
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
        "startDate": "2026-07-01T09:00:00Z", "endDate": "2026-07-08T09:00:00Z",
        "maxCensusSize": 1000,
    }}).json()

# 7. publish (async) → wait for the job
pjob = post(f"/process/{process}/publish").json()["jobId"]
while get(f"/jobs/{pjob}").json()["status"] != "completed":
    time.sleep(2)

# 8. results — addressed by the ProcessID
print(get(f"/process/{process}/results").json())
```
</details>

## Next steps

- [Members and groups](../core-concepts/members-and-groups.md) — bulk imports, groups, the members-job.
- [Census](../core-concepts/census.md) — auth-only vs. 2FA, and why auth-only publishes through a group.
- [Voting processes](../core-concepts/voting-processes.md) — election parameters, bundles, and casting.
- [Voting types](../core-concepts/voting-types.md) — single choice, approval, ranked, quadratic, and more.
