# Census

A **census** is the eligible-voter list for an election, anchored to a cryptographic **root** that the
election binds to. When you create a census you also choose **how voters prove who they are**.

## Authentication fields

- `authFields` — the fields a voter must present to authenticate (e.g. `memberNumber`). With only
  `authFields` set, the census is **auth-only**: no second factor.
- `twoFaFields` — fields used for a one-time-code second factor: `email` or `phone`.

These combine into four census types: **`auth`** (auth-only), **`mail`**, **`sms`**, and
**`sms_or_mail`**.

`authFields` options: `name`, `surname`, `memberNumber`, `nationalId`, `birthDate`.
`twoFaFields` options: `email`, `phone`.

## Creating a census

```bash
CENSUS=$(curl -s "${auth[@]}" -X POST "$B/census" \
  -d "{\"orgAddress\":\"$ORG\",\"authFields\":[\"memberNumber\"]}" | jq -r .id)
```

```jsonc
{ "id": "6a1f…" }   // carry forward: census id
```

Add `"twoFaFields":["email"]` for an email-OTP census.

<details><summary><b>C#</b> / <b>Python</b></summary>

```csharp
var census = (await Post("/census",
    new { orgAddress = org, authFields = new[] { "memberNumber" } })).GetProperty("id").GetString();
```
```python
census = post("/census", {"orgAddress": org, "authFields": ["memberNumber"]}).json()["id"]
```
</details>

## Publishing a census

Publishing locks the participant list and produces the root the election binds to. **How you publish
depends on the census type** — this is the single most common stumble.

**2FA types** (`mail` / `sms` / `sms_or_mail`) publish directly:

```bash
curl "${auth[@]}" -X POST "$B/census/$CENSUS/publish"
```

**Auth-only** censuses are **rejected** by the plain publish (`census type not found`). Publish them
**through a group** instead — this both supports auth-only and populates participants from the group:

```bash
curl "${auth[@]}" -X POST "$B/census/$CENSUS/group/$GROUP/publish" \
  -d '{"authFields":["memberNumber"],"weighted":false}'
```

Either way you get the published census:

```jsonc
{ "root": "deadbeef…", "size": 1, "uri": "https://…" }
```

The `size` is the eligible-voter count — useful later for turnout (see [Results](./results.md)). Set
`"weighted": true` to make each member's `weight` count as vote weight.

<details><summary><b>C#</b> / <b>Python</b> — auth-only via group</summary>

```csharp
await Post($"/census/{census}/group/{group}/publish",
           new { authFields = new[] { "memberNumber" }, weighted = false });
```
```python
post(f"/census/{census}/group/{group}/publish",
     {"authFields": ["memberNumber"], "weighted": False})
```
</details>

## Inspecting participants

```bash
curl "${auth[@]}" "$B/census/$CENSUS/participants"
```

```jsonc
{ "censusId": "6a1f…", "memberIds": ["…"] }
```

## Gotchas

- **Auth-only must be published via a group** (`/census/{id}/group/{groupId}/publish`). The plain
  `/publish` rejects it.
- The auth-only credential is derived from the auth field, so **`memberNumber` must be unique** across
  the census — duplicates fail at publish.
- One published census can back **multiple** processes — reuse it across votes.
