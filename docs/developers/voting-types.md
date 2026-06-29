---
title: Voting types
lead: How to shape a ballot - single choice, multiple questions, rating, approval, ranked, and weighted or quadratic voting - through the electionParams vote type. Each type is the same create-process call with different voteType fields; this page maps each one to its ballot shape and how its results read.
group: api_reference
order: 20
---

The `voteType` object inside a process's `electionParams` (see
[Voting processes](/developers/docs/voting-processes#vote-type)) shapes the ballot. A handful of fields express
every common election kind. This page gives the recipe for each: the `voteType`, the **ballot array** a
voter submits, and how to read the [results](/developers/docs/results) matrix.

## The fields

| Field | Meaning |
|-------|---------|
| `maxCount` | Number of entries (fields) in the ballot array. |
| `maxValue` | Maximum value per entry. `0` is a special marker meaning "values are amounts to aggregate" (budget/quadratic). |
| `uniqueChoices` | If `true`, no value may repeat in a ballot (used by ranked voting). |
| `costExponent` | Exponent applied per entry when computing a ballot's total cost (`2` = quadratic). |
| `maxTotalCost` / `minTotalCost` | Bounds on `Σ(value[i] ^ costExponent)` (`0` = no bound). |

A **ballot** is an array of natural numbers, one entry per field. **Results** are a histogram:
`results[field][value]` = how many voters put `value` in `field`. Per-option tallies are computed from
that — the reading differs by type (covered below and in [Results](/developers/docs/results)).

## Single choice

Pick exactly one option out of N. One field whose value is the chosen option's index.

```json
"voteType": { "maxCount": 1, "maxValue": 1 }   // N choices → maxValue = N - 1
```

- **Ballot:** `[chosenIndex]` — e.g. `[1]` to pick the option with `value: 1`.
- **Results:** `results[0]` is the per-choice count directly — `["25","17"]` means 25 chose option 0,
  17 chose option 1.

## Approval / multichoice

Approve any subset of N options. One `0/1` field **per option**.

```json
"voteType": { "maxCount": 3, "maxValue": 1, "uniqueChoices": false }   // N options → maxCount = N
```

> [!WARNING] uniqueChoices must be false
> A ballot that approves more than one option repeats the value `1`, which `uniqueChoices: true`
> rejects — leaving voters unable to select multiple options.

- **Ballot:** `[v0, v1, …, vN-1]`, each `0` or `1` — e.g. `[1,0,1]` approves options 0 and 2.
- **Results:** one field per option, each a `[#voted-0, #voted-1]` histogram. An option's approval
  count is the **second** number, `results[i][1]`:
  ```
  results = [ ["0","3"], ["1","2"] ]   // option 0 approved by 3, option 1 by 2
  ```
  To force a fixed number of approvals, set `minTotalCost = maxTotalCost = k`.

## Ranked (linear weighted)

Rank N options, each rank used once.

```json
"voteType": { "maxCount": 5, "maxValue": 4, "uniqueChoices": true }   // N options, ranks 0..N-1
```

- **Ballot:** `[rank0, rank1, …]`, a permutation — `uniqueChoices: true` enforces "no rank used twice".
- **Results:** index-weighted — for each field multiply each count by its column index and sum to get
  that option's score (see [Results](/developers/docs/results#interpretation)).

## Quadratic

Distribute credits across options; spending `v` on an option costs `v²`.

```json
"voteType": { "maxCount": 4, "maxValue": 0, "costExponent": 2, "maxTotalCost": 12 }
```

- `maxValue: 0` is the "values are aggregable amounts" marker; the per-option cap comes from
  `maxTotalCost` and `costExponent`.
- **Ballot:** `[c0, c1, c2, c3]` with `Σ ci² ≤ maxTotalCost` — e.g. `[2,2,2,0]` costs `4+4+4+0 = 12`.
- **Results:** index-weighted (the summed credits per option).

## Budget

Distribute a fixed budget across options linearly (cost = the value itself).

```json
"voteType": { "maxCount": 4, "maxValue": 0, "costExponent": 1, "maxTotalCost": 100 }
```

- **Ballot:** `[a0, a1, a2, a3]` with `Σ ai ≤ maxTotalCost`. Set `minTotalCost = maxTotalCost` to force
  spending the whole budget.
- **Results:** index-weighted (the summed amounts per option).

## Multi-question, single choice

Several independent questions, one choice each (e.g. electing CEO, COO, CFO).

```json
"voteType": { "maxCount": 3, "maxValue": 4 }   // Q questions → maxCount = Q; maxValue = max(choices)-1
```

- Provide one entry in `questions` **per field**; the ballot has one value per question.
- **Ballot:** `[choiceQ0, choiceQ1, choiceQ2]`.
- **Results:** one inner array per question; read each as per-choice counts (discrete) — see
  [Results](/developers/docs/results#interpretation).

---

For the precise protocol semantics (cost formula, `costExponent` scaling, aggregation modes), see the
Vocdoni **ballot protocol** reference on the [developer portal](https://developer.vocdoni.io/).
