---
title: Managed organizations
lead: As an integrator you can provision sub-organizations on behalf of your own customers, each isolated with its own address, members and elections, all under your integrator account.
group: integrator_platform
order: 10
---

As an integrator you provision a **managed organization** for each of your customers. Each one is an
isolated tenant — its own address, members, censuses, and elections — created and operated entirely
with your **integrator API key**, so your customers never need a Vocdoni account.

## The multi-tenant model

Your **integrator organization** is the parent account. Every managed org sits beneath it with no data
mixing, and your key acts as that org's admin. Because the integrator is resolved from the key, the
integrator endpoints are **path-less** — you never pass your own address in a URL.

The election lifecycle inside a managed org ([members](/developers/docs/members-and-groups),
[census](/developers/docs/census), [processes](/developers/docs/voting-processes)) is authorized
by your key acting as that org's admin — no extra scope beyond having created it.

## Creating a managed organization

```bash
ORG=$(curl -s "${auth[@]}" -X POST "$B/integrator/organizations" \
  -d '{"type":"association","meta":{"name":"Maple Street HOA"}}' | jq -r .address)
```

```jsonc
// carry forward: address (hex string)
{ "address": "0x4a3b…", "type": "association", "meta": { "name": "Maple Street HOA" } }
```

The on-chain account is provisioned eagerly. The optional `ownerEmail` assigns an existing user as the
managed org's admin (it defaults to your key's user). Requires the `managed:write`
[scope](/developers/docs/api-keys).

<details><summary><b>C#</b> / <b>Python</b></summary>

```csharp
var org = (await Post("/integrator/organizations",
    new { type = "association", meta = new { name = "Maple Street HOA" } })).GetProperty("address").GetString();
```
```python
org = post("/integrator/organizations",
           {"type": "association", "meta": {"name": "Maple Street HOA"}}).json()["address"]
```
</details>

## Listing managed organizations

Paginated; requires the `managed:read` scope.

```bash
curl "${auth[@]}" "$B/integrator/organizations?page=1&limit=10"
```

```jsonc
{ "organizations": [ { "address": "0x4a3b…", "meta": { "name": "Maple Street HOA" } } ],
  "pagination": { "currentPage": 1, "lastPage": 1, "totalItems": 1 } }
```

## Deleting a managed organization

Deletion **cascades**: it removes the managed org and all its off-chain data (members, censuses,
processes, bundles, CSP tokens, jobs, invites) and rolls back your usage counters, freeing the slot.

```bash
curl "${auth[@]}" -X DELETE "$B/integrator/organizations/$ORG"
```

```jsonc
{ "address": "0x4a3b…" }   // 200 OK
```

> [!WARNING] 409 if elections are still active
> Deletion is blocked while any of the org's published elections is `READY` or `PAUSED` on-chain — end
> them first (`PUT /process/{id}/status` → `ended`). A `404` means the org is already gone (safe to
> treat as success). On-chain accounts and published elections are immutable on the Vochain and are
> **not** removed — only the off-chain data is.

## Quota and usage

See how many managed orgs, processes, and census seats you've used against your limits — and whether
integrator features are enabled for your account.

```bash
curl "${auth[@]}" "$B/integrator"
```

```jsonc
{ "enabled": true,
  "limits": { "maxManagedOrgs": 1, "maxManagedProcesses": 0, "maxManagedCensusSize": 0 },
  "usage":  { "managedOrgs": 1, "managedProcesses": 3, "managedCensusSize": 42 } }
```

A `0` limit means **unlimited**. The free tier allows **one managed organization** — delete one to
free the slot, or request more quota. If `enabled` is `false`, managed organizations aren't turned on
for your plan; enable integrator access from the API Dashboard. See
[Quotas and subscriptions](/developers/docs/quotas-and-subscriptions).
