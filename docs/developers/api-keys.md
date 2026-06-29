---
title: API keys
lead: API keys let your backend authenticate without a password. Each key belongs to an organization, carries a set of scopes and an optional expiry, and can be revoked at any time.
group: integrator_platform
order: 20
---

An **API key** authenticates your backend without a password. The key *is* your integrator identity:
the server resolves your integrator organization from it, which is why the integrator endpoints take no
address in the path. Each key carries a set of **scopes** and an optional expiry, and can be revoked at
any time. API keys can only be created **under integrator organizations**.

## Using a key

Send the key as a bearer token on every request — the same header for every call in this
documentation:

```bash
curl -H "Authorization: Bearer vsk_ab12…" "$B/integrator/organizations"
```

The key's **prefix** (e.g. `vsk_ab12`) is safe to log for identification; the full secret is not.

## Scopes

A key only works on an endpoint if it carries the right scope. Mint keys with exactly what the
integration needs:

| Scope | Grants |
|-------|--------|
| `managed:write` | Create and delete managed organizations (`POST` / `DELETE /integrator/organizations…`). |
| `managed:read` | List managed organizations (`GET /integrator/organizations`). |
| `quota:read` | Read integrator quota and usage (`GET /integrator`). |

The member / census / process endpoints inside a managed org are authorized by your key acting as that
org's admin (it created it), so they need no extra scope beyond the election lifecycle itself.

> [!TIP] Least privilege
> Grant the minimum a workload needs, use a separate key per environment, and rotate keys
> periodically. The available scopes are shown in the API Dashboard during key creation.

## Creating a key

Provide a label and the required scopes; an `expiresAt` is optional. The **full secret appears only
once**, in the creation response.

```bash
curl "${auth[@]}" -X POST "$B/organizations/$INTEGRATOR/apikeys" -d '{
  "label": "CI server",
  "scopes": ["managed:write", "managed:read", "quota:read"],
  "expiresAt": "2027-01-01T00:00:00Z"
}'
```

```jsonc
{ "id": "key_123",
  "prefix": "vsk_ab12",
  "secret": "vsk_ab12….",       // shown once — store it now
  "scopes": ["managed:write", "managed:read", "quota:read"],
  "revoked": false }
```

> [!WARNING] Store the secret now
> **The secret cannot be retrieved later.** Store it in a secret manager immediately. If it's lost,
> revoke the key and create a new one — afterwards only metadata (id, prefix, scopes, timestamps)
> remains.

<details><summary><b>C#</b> / <b>Python</b></summary>

```csharp
var key = await Post($"/organizations/{integrator}/apikeys", new {
    label = "CI server",
    scopes = new[] { "managed:write", "managed:read", "quota:read" },
});
var secret = key.GetProperty("secret").GetString();   // store immediately
```
```python
key = post(f"/organizations/{integrator}/apikeys", {
    "label": "CI server",
    "scopes": ["managed:write", "managed:read", "quota:read"],
}).json()
secret = key["secret"]   # store immediately
```
</details>

## Listing and revoking

List your keys to review scopes and last use; revoke a key immediately when it's no longer needed or
possibly compromised.

```bash
curl "${auth[@]}" "$B/organizations/$INTEGRATOR/apikeys"                  # list
curl "${auth[@]}" -X DELETE "$B/organizations/$INTEGRATOR/apikeys/$KEYID" # revoke
```

Listing returns only metadata (id, prefix, scopes, timestamps) — never the secret.

## Authentication failure modes

| Status | Meaning | Fix |
|--------|---------|-----|
| `401 Unauthorized` | Missing or invalid key. | Check the `Authorization` header and key value. |
| `403` insufficient scope | The key lacks the endpoint's scope. | Re-mint with the needed scope (`managed:write` / `managed:read` / `quota:read`). |
| `403` not an integrator | The resolved organization isn't an integrator. | Use a key minted under your integrator organization. |
