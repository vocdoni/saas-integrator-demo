# Results

Results are **public** (no auth) and available both while a process runs (a live tally) and after it
ends (final). You address them by **ProcessID**.

```bash
curl -s "$B/process/$PROCESS/results"
```

```jsonc
{ "status": "RESULTS",
  "finalResults": true,
  "voteCount": 42,
  "startDate": "2026-07-01T09:00:00Z",
  "endDate":   "2026-07-03T18:00:00Z",
  "results": [ ["25", "17"] ] }
```

- `finalResults: false` → the process is still open; the tally is provisional.
- `finalResults: true` → voting has ended; results are final.

<details><summary><b>C#</b> / <b>Python</b></summary>

```csharp
var r = await Get($"/process/{process}/results");
int votes = r.GetProperty("voteCount").GetInt32();
```
```python
r = get(f"/process/{process}/results").json()
votes = r["voteCount"]
```
</details>

## The results matrix

`results` is a matrix of strings (tallies can be large or weighted): `results[field][value]` = the
number of voters who put `value` in that field. For a single yes/no question with choices
`Yes (value 0)` and `No (value 1)`:

```
results[0] = ["25", "17"]
              └Yes  └No        → 25 voted Yes, 17 voted No  (voteCount = 42)
```

## Interpretation

The matrix is a raw histogram; clients turn it into per-option numbers in one of two ways, picked by
the voting type:

- **Discrete** (count per choice) — the common case for single choice and multi-question. Each inner
  array is read directly as the per-choice counts.
- **Index-weighted** — for each field, multiply each count by its column index and sum. Used by
  ranked, quadratic, budget, and rating ballots, where the *value* carries meaning.

**Approval / multichoice reads differently again.** There the matrix has **one field per option**,
each a `[#voted-0, #voted-1]` histogram, so an option's count is the **second** number, `results[i][1]`
— not `results[0]`:

```
results = [ ["0","3"], ["1","2"] ]    # options Yes / No, 3 ballots
            └Yes        └No
Yes approved by results[0][1] = 3 ;  No approved by results[1][1] = 2
```

Reading `results[0]` here (`["0","3"]`) as "Yes 0, No 3" is the classic mistake — each voter can
approve several options, so iterate the fields, not one field's values. See
[Voting types](./voting-types.md) for which reading each ballot uses.

## Turnout and the census size

`voteCount` is how many ballots were cast. To show **turnout** — what share of the *electorate* voted
— divide by the eligible-voter count, which is the **published census size**. The results payload does
not carry it; read it from the process detail (`census.size` on `GET /process/{id}`) or remember the
`size` returned when you [published the census](./census.md#publishing-a-census). A bar that fills
`votesForOption / censusSize` reads as turnout share; one that fills against the leading option always
shows the winner at 100%, which hides participation.
