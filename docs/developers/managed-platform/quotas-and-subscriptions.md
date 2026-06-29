# Quotas and subscriptions

Each organization has a **subscription** that defines the features it can use and its usage limits.
Read it to adapt your integration — gate options in your UI, show plan limits to users, and avoid
hitting a wall mid-flow.

## Reading the subscription

```bash
curl "${auth[@]}" "$B/organizations/$ORG/subscription"
```

```jsonc
{ "plan": { ... },
  "subscriptionDetails": { ... },
  "usage": { ... } }                 // counters: processes run, members imported, etc.
```

<details><summary><b>C#</b> / <b>Python</b></summary>

```csharp
var sub = await Get($"/organizations/{org}/subscription");
bool live = sub.GetProperty("plan").GetProperty("liveResults").GetBoolean();
```
```python
sub = get(f"/organizations/{org}/subscription").json()
live = sub["plan"]["liveResults"]
```
</details>

## Plan features

Features indicate what a plan unlocks; check them before showing options in the UI.

| Feature | Meaning |
|---------|---------|
| `anonymous` | Anonymous voting with zero-knowledge proofs. |
| `liveResults` | Live results while a process is running. |
| `whiteLabel` | White-label branding for the voting experience. |
| `overwrite` | Allow voters to change their vote. |
| `2FAemail` (integer) | Quota of email second-factor messages. |
| `2FAsms` (integer) | Quota of SMS second-factor messages. |

## Subscription details

The `subscriptionDetails` object reports the subscription status, plan, census ceiling, and key dates.

| Field | Meaning |
|-------|---------|
| `active` (boolean) | Whether the subscription is currently active. |
| `planId` (integer) | The plan the organization is on. |
| `maxCensusSize` (integer) | The largest census the plan permits. |
| `renewalDate` (string) | When the subscription next renews. |

## Integrator quota

As an integrator you also have **provisioning limits**, separate from any single org's subscription —
read them from `GET /integrator` (see
[Managed organizations → Quota and usage](./managed-organizations.md#quota-and-usage)):

- The **free tier allows one managed organization**; deleting it frees the slot (the cascade rolls back
  your usage counters).
- The per-managed-org limits — members and process drafts — are governed by your integrator plan and
  may be `0` until your plan grants them; enable integrator access or upgrade from the API Dashboard.
- A `0` limit in the `GET /integrator` response means **unlimited**.
