---
title: Members and groups
lead: Members are the people in your organization. Import them once, organize them into groups, and reuse them to build censuses for many elections.
group: core_concepts
order: 20
---

**Members** are an organization's people — your customer's voters. **Groups** are named subsets of
members, and a group is also the **bridge that lets you publish an auth-only census** (see
[Census](/developers/docs/census)).

## Adding members

Member imports are **bulk and asynchronous**: the call returns a `jobId`, and you poll a members-job
until it reports `progress: 100`.

```bash
JOB=$(curl -s "${auth[@]}" -X POST "$B/organizations/$ORG/members" -d '{
  "members": [
    { "name": "Alice", "surname": "Doe", "email": "alice@example.org",
      "memberNumber": "A-101", "weight": "1" }
  ]
}' | jq -r .jobId)

# poll the members-job until done
until [ "$(curl -s "${auth[@]}" "$B/organizations/$ORG/members/job/$JOB" | jq -r .progress)" = "100" ]; do sleep 1; done
```

```jsonc
// GET /organizations/{addr}/members/job/{jobId}
{ "added": 1, "total": 1, "progress": 100, "errors": [] }   // progress == 100 → done
```

Member fields: `name`, `surname`, `email`, `phone`, `memberNumber`, `nationalId`, `birthDate`, and
`weight` (a **string**, e.g. `"1"`; weighted censuses use it as the vote weight).

<details><summary><b>C#</b></summary>

```csharp
var job = (await Post($"/organizations/{org}/members",
    new { members = new[] { new { name = "Alice", memberNumber = "A-101", weight = "1" } } }))
    .GetProperty("jobId").GetString();
while ((await Get($"/organizations/{org}/members/job/{job}")).GetProperty("progress").GetInt32() < 100)
    await Task.Delay(1000);
```
</details>

<details><summary><b>Python</b></summary>

```python
job = post(f"/organizations/{org}/members",
           {"members": [{"name": "Alice", "memberNumber": "A-101", "weight": "1"}]}).json()["jobId"]
while get(f"/organizations/{org}/members/job/{job}").json()["progress"] < 100:
    time.sleep(1)
```
</details>

> [!WARNING] Wait for the import job
> Don't build the census until the members-job reaches `progress: 100` — the participants won't be
> there yet. See [Jobs](/developers/docs/jobs) for the full job model.

## Listing members

The list is **paginated** (default `limit` is small). Walk every page so large memberbases aren't
silently truncated.

```bash
curl "${auth[@]}" "$B/organizations/$ORG/members?page=1&limit=100"
```

```jsonc
{ "members": [ { "id": "…", "memberNumber": "A-101", "name": "Alice" } ],
  "pagination": { "currentPage": 1, "lastPage": 1, "totalItems": 1 } }
```

<details><summary><b>Python</b> — walk every page</summary>

```python
members, page = [], 1
while True:
    r = get(f"/organizations/{org}/members?page={page}&limit=100").json()
    members += r["members"]
    p = r.get("pagination")
    if not r["members"] or not p or p["currentPage"] >= p["lastPage"]:
        break
    page += 1
```
</details>

## Deleting members

Note the **plural** path and a body of `ids`. The singular `/member` returns 404 on the deployed
backend.

```bash
curl "${auth[@]}" -X DELETE "$B/organizations/$ORG/members" -d '{"ids":["<memberId>"]}'
```

## Groups

A group is a named subset of members. The common case is an **all-members group**, which is what you
publish an auth-only census through.

```bash
GROUP=$(curl -s "${auth[@]}" -X POST "$B/organizations/$ORG/groups" \
  -d '{"title":"All voters","includeAllMembers":true}' | jq -r .id)
```

```jsonc
{ "id": "665f…" }   // carry forward: group id
```

<details><summary><b>C#</b> / <b>Python</b></summary>

```csharp
var group = (await Post($"/organizations/{org}/groups",
    new { title = "All voters", includeAllMembers = true })).GetProperty("id").GetString();
```
```python
group = post(f"/organizations/{org}/groups",
             {"title": "All voters", "includeAllMembers": True}).json()["id"]
```
</details>

## Gotchas

- Adding members is a **job** — wait for `progress: 100` before building a census.
- Listing is **paginated** — walk the pages.
- Delete is `DELETE /organizations/{addr}/members` (**plural**), with `{ "ids": [...] }`.
- For an **auth-only** census, each `memberNumber` must be **unique** — it becomes the voting
  credential (see [Census](/developers/docs/census)).
