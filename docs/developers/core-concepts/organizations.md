# Organizations

An **organization** is the tenant that owns members, censuses, and elections. As an integrator you
don't operate one shared org — you create one **managed organization per customer** and run everything
inside it with your integrator key. This page covers what an organization is and how to read and
update it; creating and deleting managed organizations is covered in
[Managed organizations](../managed-platform/managed-organizations.md).

## Anatomy

| Field | Meaning |
|-------|---------|
| `address` | The organization's on-chain account, a **hex string**. It identifies the org in every path and is the value you carry forward after creation. |
| `type` | A free-form classification (e.g. `association`, `company`, `cooperative`). |
| `meta` | A free-form metadata map — at minimum a `name`. Multilingual values are objects keyed by language with a `default`. |

Addresses are **hex strings everywhere** — never integer arrays, regardless of how a schema renders
them.

## Reading an organization

```bash
curl "${auth[@]}" "$B/organizations/$ORG"
```

```jsonc
{ "address": "0x4a3b…", "type": "association", "meta": { "name": "Maple Street HOA" } }
```

<details><summary><b>C#</b></summary>

```csharp
var org = await Get($"/organizations/{address}");
var name = org.GetProperty("meta").GetProperty("name").GetString();
```
</details>

<details><summary><b>Python</b></summary>

```python
org = get(f"/organizations/{address}").json()
name = org["meta"]["name"]
```
</details>

## Updating organization info

Update the descriptive metadata (name, type, and other `meta` fields). On-chain identity — the
`address` — never changes.

```bash
curl "${auth[@]}" -X PUT "$B/organizations/$ORG" \
  -d '{"type":"association","meta":{"name":"Maple Street HOA","city":"Springfield"}}'
```

## The integrator relationship

Your **integrator organization** is the parent account; each managed organization is an isolated
tenant beneath it, with its own address, members, censuses, and elections. Customers never need a
Vocdoni account — your integrator key acts as the admin of every org it creates.

- To **provision** a managed org, see [Managed organizations](../managed-platform/managed-organizations.md).
- To add people to it, see [Members and groups](./members-and-groups.md).
- To check how many orgs/processes/seats you've used against your limits, see
  [Quotas and subscriptions](../managed-platform/quotas-and-subscriptions.md).
