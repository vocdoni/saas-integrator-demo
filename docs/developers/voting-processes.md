---
title: Voting processes
lead: A process is an election - a set of questions run against a published census, with rules for how votes are cast and when voting opens and closes.
group: core_concepts
order: 40
---

A **process** is an election: one or more questions run against a published [census](/developers/docs/census),
governed by rules about how votes are cast and when voting opens and closes. You create it off-chain
(fully editable at first), **publish** it on-chain, voters cast ballots, and you read
[results](/developers/docs/results).

One **ProcessID** identifies the election throughout. `POST /process` returns it as a bare JSON
string, and you reuse the same id for publish, status, results, metadata, and bundling — before and
after publishing.

## Creating a process

Bind the process to a published census and describe it with election parameters. Titles and
descriptions are **multilingual** objects keyed by language, each with a `default`.

```bash
PROCESS=$(curl -s "${auth[@]}" -X POST "$B/process" -d "{
  \"orgAddress\":\"$ORG\",\"censusId\":\"$CENSUS\",
  \"metadata\":{\"title\":\"Board election 2026\"},
  \"electionParams\":{
    \"title\":{\"default\":\"Board election 2026\"},
    \"description\":{\"default\":\"Elect the new board\"},
    \"questions\":[{\"title\":{\"default\":\"Who should chair the board?\"},
      \"choices\":[{\"title\":{\"default\":\"Ada Lovelace\"},\"value\":0},
                   {\"title\":{\"default\":\"Alan Turing\"},\"value\":1}]}],
    \"voteType\":{\"maxCount\":1,\"maxValue\":1},
    \"electionType\":{\"autostart\":true,\"interruptible\":true},
    \"startDate\":\"2026-07-01T09:00:00Z\",\"endDate\":\"2026-07-03T18:00:00Z\",
    \"maxCensusSize\":1000
  }}" | jq -r .)   # bare JSON string → the ProcessID
```

### Election parameters

| Field | Type | Meaning |
|-------|------|---------|
| `title`, `description` | multilang | Shown to voters. `{ "default": "…", "es": "…" }`. |
| `startDate`, `endDate` | string (ISO 8601) | Voting window. |
| `electionType` | object | Behavioral flags — see below. |
| `voteType` | object | Ballot shape — see [Voting types](/developers/docs/voting-types). |
| `questions` | array | Each has a `title` and `choices` (each choice a `title` + numeric `value`). |
| `maxCensusSize` | integer | Upper bound on eligible voters for the process. |

### Election type flags

- `autostart` — open voting automatically at `startDate`.
- `interruptible` — allow pausing or ending the process early.
- `anonymous` — hide *who* voted using zero-knowledge proofs.
- `dynamicCensus` — allow the census to change after the process starts.
- `secretUntilTheEnd` — keep results hidden until voting closes (encrypted ballots).

### Vote type

`voteType` shapes the ballot — single choice, approval/multichoice, ranked, quadratic, budget, or
multi-question. Each shape is a specific combination of `maxCount`, `maxValue`, `uniqueChoices`, and
cost fields. See **[Voting types](/developers/docs/voting-types)** for the recipe and ballot shape of each.

<details><summary><b>C#</b> / <b>Python</b> — create</summary>

```csharp
var process = (await Post("/process", new {
    orgAddress = org, censusId = census,
    metadata = new { title = "Board election 2026" },
    electionParams = new {
        title = new { @default = "Board election 2026" },
        questions = new[] { new { title = new { @default = "Who should chair the board?" },
            choices = new[] { new { title = new { @default = "Ada Lovelace" }, value = 0 },
                              new { title = new { @default = "Alan Turing" }, value = 1 } } } },
        voteType = new { maxCount = 1, maxValue = 1 },
        electionType = new { autostart = true, interruptible = true },
        startDate = "2026-07-01T09:00:00Z", endDate = "2026-07-03T18:00:00Z",
        maxCensusSize = 1000,
    }})).GetString();
```
```python
process = post("/process", {
    "orgAddress": org, "censusId": census,
    "metadata": {"title": "Board election 2026"},
    "electionParams": {
        "title": {"default": "Board election 2026"},
        "questions": [{"title": {"default": "Who should chair the board?"},
                       "choices": [{"title": {"default": "Ada Lovelace"}, "value": 0},
                                   {"title": {"default": "Alan Turing"}, "value": 1}]}],
        "voteType": {"maxCount": 1, "maxValue": 1},
        "electionType": {"autostart": True, "interruptible": True},
        "startDate": "2026-07-01T09:00:00Z", "endDate": "2026-07-03T18:00:00Z",
        "maxCensusSize": 1000,
    }}).json()
```
</details>

## Publishing on-chain

Publishing is **asynchronous**: it returns a `jobId` (or `200` directly if already published). Poll
the [job](/developers/docs/jobs) until it completes.

```bash
PJOB=$(curl -s "${auth[@]}" -X POST "$B/process/$PROCESS/publish" | jq -r .jobId)
until [ "$(curl -s "$B/jobs/$PJOB" | jq -r .status)" = "completed" ]; do sleep 2; done
```

The job's `result.address` is the **on-chain election id**. You keep addressing the process by its
**ProcessID** for everything server-side; the on-chain id surfaces only client-side, when a voter
signs a ballot.

## Changing status

Status changes (`ready`, `paused`, `ended`, `canceled`) are also asynchronous. Address by ProcessID.

```bash
curl "${auth[@]}" -X PUT "$B/process/$PROCESS/status" -d '{"status":"ended"}'
```

```jsonc
{ "jobId": "d4e5f6…" }   // 202 — poll /jobs/{jobId}
```

## Process bundles

A **bundle** groups one or more processes under a census and is the **voter-facing entry point** for
casting. Reference each process by its **ProcessID**.

```bash
BUNDLE_URI=$(curl -s "${auth[@]}" -X POST "$B/process/bundle" \
  -d "{\"censusId\":\"$CENSUS\",\"processes\":[\"$PROCESS\"]}" | jq -r .uri)
BUNDLE="${BUNDLE_URI##*/}"   # bundleId is the last path segment
```

```jsonc
{ "root": "deadbeef…", "uri": "https://…/process/bundle/<bundleId>" }
```

Bundles are useful when an assembly votes on several motions in one session.

## Casting a vote

Voting is voter-facing and cryptographic — the one place you hand off to the **client-side SDK** in
the voter's browser. The REST API **authenticates** the voter and **relays** an already-signed vote;
it never builds or signs the ballot. The steps:

1. **Authenticate against the bundle.** Step `0` identifies the voter with exactly the fields the
   census `authFields` require; for a 2FA census, step `1` submits the one-time code. For an auth-only
   census, step `0` returns a verified token and there is no code.
   ```bash
   # step 0 — identify (auth-only census → already verified)
   curl -X POST "$B/process/bundle/$BUNDLE/auth/0" -H "Content-Type: application/json" \
     -d '{"memberNumber":"A-101"}'
   # step 1 — submit the OTP (2FA censuses only); authData[0] is the code
   curl -X POST "$B/process/bundle/$BUNDLE/auth/1" -H "Content-Type: application/json" \
     -d '{"authToken":"<token>","authData":["123456"]}'
   ```
   ```jsonc
   { "authToken": "deadbeef…" }
   ```

2. **CSP-sign the ballot.** With a verified `authToken`, the CSP ECDSA-signs the voter's ephemeral
   address for the chosen election (the on-chain id). The SDK generates the ephemeral key and encodes
   the ballot.
   ```bash
   curl -X POST "$B/process/bundle/$BUNDLE/sign" -H "Content-Type: application/json" \
     -d '{"authToken":"deadbeef…","electionId":"0x9f2c…","payload":"<ephemeral addr>"}'
   ```
   ```jsonc
   { "signature": "…", "weight": "1" }   // CSP signature + the voter's census weight
   ```
   Each token can sign each process **once** — no double-voting.

3. **Relay the signed vote** (public, async). The signed envelope names the process, so the path
   carries no id.
   ```bash
   curl -X POST "$B/vote" -H "Content-Type: application/json" -d '{"txPayload":"<signed vote tx>"}'
   ```
   ```jsonc
   { "jobId": "…" }   // poll /jobs/{jobId}; result.voteID is the vote nullifier
   ```

### From the browser with the SDK

In practice you don't call these endpoints by hand — the
[`@vocdoni/integrator-sdk`]({{SDK_URL}}) wraps all of them. You need the
**apiUrl**, the **bundleId**, and the **ProcessID**; the SDK resolves the on-chain id, generates the
ephemeral identity, encodes the ballot, signs, relays, and polls:

```ts
import { VocdoniApiClient } from '@vocdoni/api-client'
import { VotingClient, EphemeralSigner } from '@vocdoni/api-voting'

const client = new VocdoniApiClient({ apiUrl })

const bundle = await client.bundle.get(bundleId)
let { authToken } = await client.bundle.authStep0(bundleId, { memberNumber })
if ((bundle.census?.twoFaFields?.length ?? 0) > 0)
  ({ authToken } = await client.bundle.authStep1(bundleId, { authToken, authData: [otp] }))

const election = await client.elections.get(processId)   // processId = the 24-hex ProcessID
const signer = new EphemeralSigner()
const { signature, weight } = await client.bundle.sign(
  bundleId, { authToken, electionId: election.address, payload: signer.address })

const jobId = await new VotingClient({ client }).vote({
  processId: election.address,
  choices,                       // ballot array — see Voting types
  chainId: election.chainId,
  signer, cspSignature: signature, cspWeight: weight,
  encryptionKeys: election.encryptionPublicKeys,   // only for secret-until-the-end elections
})
const nullifier = (await client.jobs.waitFor(jobId)).result?.voteID
```

The `choices` array is the on-chain ballot — its shape depends on the voting type. See
**[Voting types](/developers/docs/voting-types)**.

## Gotchas

- `POST /process` returns a **bare string** (the ProcessID), not an object.
- Publish and status changes are **jobs** — read the outcome from `/jobs/{jobId}`, not the POST body.
- Address the process by its **ProcessID** everywhere server-side (status, results, metadata, bundle);
  the on-chain id is only needed client-side, to sign voter payloads.
