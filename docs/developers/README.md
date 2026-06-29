# Vocdoni Integrator Documentation

Add secure, anonymous, and end-to-end verifiable voting to your own product. These pages document the
Vocdoni **SaaS API** from an integrator's point of view: provision organizations for your customers,
manage their voters, run elections, and read verifiable results — all from your backend over plain
HTTP, with vote casting handled client-side by the SDK.

Every code example is shown in **bash + curl**, **.NET (C#)**, and **Python**.

## Get started

- [Quickstart](./get-started/quickstart.md) — the whole lifecycle once, end to end.
- [Developer tools](./get-started/developer-tools.md) — when to use the REST API vs. the SDK, and where the references live.

## Core concepts

- [Organizations](./core-concepts/organizations.md)
- [Members and groups](./core-concepts/members-and-groups.md)
- [Census](./core-concepts/census.md)
- [Voting processes](./core-concepts/voting-processes.md)
  - [Voting types](./core-concepts/voting-types.md)
- [Results](./core-concepts/results.md)
- [Jobs](./core-concepts/jobs.md)

## Managed platform

- [Managed organizations](./managed-platform/managed-organizations.md)
- [API keys](./managed-platform/api-keys.md)
- [Quotas and subscriptions](./managed-platform/quotas-and-subscriptions.md)

---

### How the API fits together

Five building blocks compose every integration:

| Block | What it is |
|-------|------------|
| **Organization** | A tenant that owns members, censuses, and elections. As an integrator you create one **managed organization** per customer. |
| **Members & groups** | The people in an organization, and named subsets of them. |
| **Census** | The eligible-voter list for an election, anchored to a cryptographic root, plus *how* voters authenticate. |
| **Process** | An election: questions over a published census, opened and closed on-chain. |
| **Results** | The tally, readable live and final, verifiable against the protocol. |

Anything that touches the chain (publishing, status changes, vote relay, bulk member imports) runs
**asynchronously** and returns a **job id** you poll until completion — see [Jobs](./core-concepts/jobs.md).

The base URL and bearer auth are the same for every call; see the [Quickstart](./get-started/quickstart.md#set-up-a-client).
