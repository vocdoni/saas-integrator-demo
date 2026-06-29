# Jobs

Anything that touches the chain — publishing a process, changing its status, relaying a vote — and
bulk member imports run **asynchronously**. The write returns a **`jobId`**, and you poll one endpoint
to learn the outcome. This is the async spine of the API.

## Polling a job

```bash
curl -s "$B/jobs/$JOBID"     # public — the job id is the capability
```

```jsonc
{ "jobId": "a1b2c3…",
  "type": "publish_process",          // org_members | census_participants | publish_process |
                                      //   set_process_status | relay_vote
  "status": "completed",              // pending | completed | failed
  "result": { "address": "0x9f2c…",   // on publish: the on-chain election id
              "status": "READY",      // on status change: the new status
              "voteID": "" },         // on relay_vote: the vote nullifier
  "error": "" }                       // populated only when status == failed
```

Rules of thumb:

- The call always returns `200`, even for failures — branch on the **`status`** field.
- `completed` → read `result`; `failed` → read `error` and **fail fast** (don't keep polling); anything
  else → keep polling (every ~2s is plenty).

<details><summary><b>C#</b> / <b>Python</b> — poll to completion</summary>

```csharp
JsonElement job;
do { await Task.Delay(2000); job = await Get($"/jobs/{jobId}"); }
while (job.GetProperty("status").GetString() == "pending");
if (job.GetProperty("status").GetString() == "failed")
    throw new Exception(job.GetProperty("error").GetString());
```
```python
while True:
    job = get(f"/jobs/{jobId}").json()
    if job["status"] == "completed": break
    if job["status"] == "failed": raise RuntimeError(job["error"])
    time.sleep(2)
```
</details>

## The members-job

Bulk member adds report a richer, progress-based shape instead of the generic job above. Poll it on a
dedicated path:

```bash
curl -s "${auth[@]}" "$B/organizations/$ORG/members/job/$JOBID"
```

```jsonc
{ "added": 120, "total": 200, "progress": 60, "errors": [] }   // progress == 100 → done
```

Wait for `progress: 100` (and an empty `errors`) before building a census from the members. See
[Members and groups](./members-and-groups.md#adding-members).
