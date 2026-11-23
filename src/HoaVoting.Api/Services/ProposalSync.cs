using System.Globalization;
using System.Net;
using HoaVoting.Api.Data;
using HoaVoting.Api.Models;
using HoaVoting.Api.Services.Vocdoni;
using Microsoft.EntityFrameworkCore;

namespace HoaVoting.Api.Services;

public enum PublishOutcome { Open, Pending, Failed }

/// <summary>
/// Keeps a local <see cref="Proposal"/> in step with its upstream process. Publishing is pay-per-process
/// (saas-backend #819) and its job can stay pending for hours, so the local row is the record of what
/// upstream did: it is saved before the publish call, and every save that follows an upstream side
/// effect uses <see cref="CancellationToken.None"/> so a client disconnect cannot drop it.
/// </summary>
internal static class ProposalSync
{
    /// <summary>
    /// The publish routine (create and retry): publish, follow the job for the bounded poll, read back.
    /// A refusal deletes the upstream draft when <paramref name="deleteOnRefusal"/> (create) and the row
    /// with it; otherwise, or when that delete fails, the row turns Failed. Either way the refusal is
    /// rethrown (→ 502 with the readable detail). No answer at all (transport error, timeout, cancel)
    /// propagates with the row still Draft and no job id: the reconcile settles it from the process read.
    /// </summary>
    public static async Task<PublishOutcome> PublishAsync(
        AppDbContext db, IVocdoniClient vocdoni, Proposal p, bool deleteOnRefusal, CancellationToken ct)
    {
        string? jobId;
        try
        {
            jobId = await vocdoni.PublishVotingProcessAsync(p.VocdoniProcessId, ct);
        }
        catch (VocdoniApiException e)
        {
            if (deleteOnRefusal && await TryDeleteUpstreamAsync(vocdoni, p.VocdoniProcessId))
                db.Proposals.Remove(p);
            else
                MarkFailed(p, UpstreamErrors.Describe(e).Detail);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        if (jobId is not null)
        {
            p.PublishJobId = jobId;
            await db.SaveChangesAsync(CancellationToken.None);

            (JobState State, string? Error) job;
            try
            {
                job = await vocdoni.WaitForJobAsync(jobId, ct);
            }
            catch (Exception e) when (e is VocdoniApiException or HttpRequestException)
            {
                return PublishOutcome.Pending; // job id is stored; the reconcile follows it
            }

            if (job.State == JobState.Pending) return PublishOutcome.Pending;
            if (job.State == JobState.Failed)
            {
                // Never auto-delete here: the job may have submitted elections, and upstream refuses to
                // delete such a draft (409 40903/40904). Retry publish adopts them at no new charge.
                MarkFailed(p, string.IsNullOrEmpty(job.Error) ? "The publish job failed." : job.Error);
                await db.SaveChangesAsync(CancellationToken.None);
                return PublishOutcome.Failed;
            }
        }

        VotingProcessResponse proc;
        try
        {
            proc = await vocdoni.GetVotingProcessAsync(p.VocdoniProcessId, ct);
        }
        catch (Exception e) when (e is VocdoniApiException or HttpRequestException)
        {
            return PublishOutcome.Pending; // published but not read back: the reconcile promotes it
        }
        if (!IsFullyPublished(p, proc)) return PublishOutcome.Pending; // the read lags the job: same
        MarkOpen(p, proc);
        await db.SaveChangesAsync(CancellationToken.None);
        return PublishOutcome.Open;
    }

    /// <summary>
    /// Read-side reconcile of one proposal, in order: settle a Draft (promote from a process read that
    /// proves the publish landed, or fail it from its job), copy upstream's end date, refresh question
    /// status, then close an Open proposal whose questions all ended or whose end date passed. It only
    /// promotes or fails rows, never deletes. Returns the upstream questions keyed by on-chain id (tallies).
    /// The caller saves (<see cref="SaveAsync"/>).
    /// </summary>
    public static async Task<Dictionary<string, VotingProcessQuestion>> ReconcileAsync(
        IVocdoniClient vocdoni, Proposal p, CancellationToken ct)
    {
        VotingProcessResponse? proc = null;
        try
        {
            proc = await vocdoni.GetVotingProcessAsync(p.VocdoniProcessId, ct);
        }
        catch (Exception e) when (e is VocdoniApiException or HttpRequestException) { /* serve stored values */ }

        // 1. Draft: only a read that says published, with an upstream id for every local question, promotes.
        if (p.Status == ProposalStatus.Draft)
        {
            if (proc is not null && IsFullyPublished(p, proc))
                MarkOpen(p, proc);
            else if (p.PublishJobId is { } jobId)
            {
                try
                {
                    var job = await vocdoni.GetJobAsync(jobId, ct);
                    if (job.Status == "failed") MarkFailed(p, job.Errors is { Count: > 0 } errs ? string.Join("; ", errs) : "The publish job failed.");
                }
                catch (Exception e) when (e is VocdoniApiException or HttpRequestException) { /* 404 / unreachable: stay Draft */ }
            }
        }

        var byUpstream = new Dictionary<string, VotingProcessQuestion>();
        if (proc is not null)
        {
            // 2. End date: upstream wins, so an extension whose job outlived its request shows up here.
            if (DateTimeOffset.TryParse(proc.EndDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
                && end != p.EndDate)
                p.EndDate = end;

            // 3. Question status.
            foreach (var q in proc.Questions)
                if (!string.IsNullOrEmpty(q.UpstreamId)) byUpstream[q.UpstreamId!] = q;
            foreach (var q in p.Questions)
                if (q.UpstreamId != "" && byUpstream.TryGetValue(q.UpstreamId, out var pq) && !string.IsNullOrEmpty(pq.Status))
                    q.Status = pq.Status!;
        }

        // ponytail: closing is one-way. An extension whose job outlives the old end date can leave a
        // live proposal Closed here; reopen from the process read (end in the future, a question not
        // ended) if that shows up.
        // 4. Closed: Open proposals only — a Draft or Failed one past its end date stays resolvable.
        if (p.Status == ProposalStatus.Open
            && ((p.Questions.Count > 0 && p.Questions.All(q => IsEnded(q.Status))) || p.EndDate < DateTimeOffset.UtcNow))
            p.Status = ProposalStatus.Closed;

        return byUpstream;
    }

    /// <summary>Saves what the reconcile changed; a row deleted meanwhile (create cleanup, DELETE) is not an error.</summary>
    public static async Task SaveAsync(AppDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException) { /* serve what we read; the next read is consistent */ }
    }

    private static bool IsFullyPublished(Proposal p, VotingProcessResponse proc) =>
        proc.Published && p.Questions.All(q => !string.IsNullOrEmpty(proc.Questions.ElementAtOrDefault(q.Order)?.UpstreamId));

    // Questions are matched to the process read by order (the order they were authored in).
    private static void MarkOpen(Proposal p, VotingProcessResponse proc)
    {
        foreach (var q in p.Questions)
        {
            var pq = proc.Questions.ElementAtOrDefault(q.Order);
            q.UpstreamId = pq?.UpstreamId ?? "";
            q.Status = pq?.Status ?? "";
        }
        p.ChainId = proc.ChainId ?? "";
        p.Status = ProposalStatus.Open;
        p.PublishError = null;
    }

    private static void MarkFailed(Proposal p, string error)
    {
        p.Status = ProposalStatus.Failed;
        p.PublishError = error;
    }

    // Cleanup after a refused create: true when the draft is gone upstream (deleted, or already 404).
    // CancellationToken.None: a disconnect must not leave an orphan draft behind.
    private static async Task<bool> TryDeleteUpstreamAsync(IVocdoniClient vocdoni, string processId)
    {
        try
        {
            await vocdoni.DeleteVotingProcessAsync(processId, CancellationToken.None);
            return true;
        }
        catch (VocdoniApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (Exception e) when (e is VocdoniApiException or HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    public static bool IsEnded(string status) => status.ToUpperInvariant() is "ENDED" or "CANCELED" or "RESULTS";
}
