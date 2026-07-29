import { VocdoniApiClient, JobFailedError } from '@vocdoni/api-client'
import { buildVoteTransaction, EphemeralSigner } from '@vocdoni/api-voting'

// Casts ballots on a multi-question voting process (saas-backend #571) entirely through the SaaS API.
// The voter authenticates ONCE against the process census, CSP-signs and builds one vote envelope per
// question (each question is its own on-chain election), then relays them all in a single batch
// (POST /votes, saas-backend #610). Relaying one by one used to leave a window in which some questions
// were on chain and the rest were not — no rollback, no retry — so a failure half-voted the ballot.
// The batch is validated and enqueued all or nothing, which closes that window.
//
// `answers` = [{ upstreamId, choices }] — `choices` is the on-chain ballot array for that question:
// single → [chosenIndex]; multiple → [v0..vN-1] (1 per pick); ranked → [v0..vN-1] (unique rank values).
//
// Returns one outcome per answered question, in `answers` order:
//   [{ upstreamId, ok, voteID, error }]
// A batch rejected synchronously (bad envelope, unknown process, repeated nullifier, queue full)
// throws instead — nothing was relayed, so the caller can fix and retry safely.
export async function castProcessVotes({ apiUrl, processId, chainId, memberNumber, answers }) {
  const client = new VocdoniApiClient({ apiUrl })

  // 1. Auth once per process. Auth-only census ⇒ the step-0 token is already verified (no OTP).
  const { authToken } = await client.processes.authStep0(processId, { memberNumber })
  if (!authToken) throw new Error('Authentication failed — check your member number.')

  // 2. Per answered question: CSP-sign a fresh ephemeral identity for that election and build the
  //    signed envelope. Still one CSP round trip per question — the batch collapses the relays and
  //    the job polls, not the signatures. Nothing is relayed in this phase.
  const envelopes = []
  for (const { upstreamId, choices } of answers) {
    const signer = new EphemeralSigner()
    const { signature, weight } = await client.processes.sign(processId, {
      authToken,
      payload: signer.address,
      electionId: upstreamId, // the target question's on-chain election id
    })
    envelopes.push({
      upstreamId,
      txPayload: buildVoteTransaction({
        processId: upstreamId,
        choices,
        chainId,
        signer,
        cspSignature: signature,
        cspWeight: weight,
      }),
    })
  }

  // 3. Relay the whole ballot in one call. A process caps at 100 questions and a batch at 100 votes,
  //    so a full ballot always fits — no chunking needed.
  const { jobId } = await client.elections.voteBatch({
    votes: envelopes.map((e) => ({ txPayload: e.txPayload })),
  })

  // 4. One job covers the batch. It only completes when every envelope succeeded and fails otherwise,
  //    but a failed job still carries the per-envelope truth — so read the outcomes either way.
  let job
  try {
    job = await client.jobs.waitFor(jobId)
  } catch (e) {
    if (!(e instanceof JobFailedError)) throw e // network error or poll timeout: no outcomes to report
    job = e.job
  }

  // `result.votes` is index-aligned with the request. Guard for a short/missing array so a malformed
  // job can never yield undefined rows.
  const votes = job.result?.votes ?? []
  return envelopes.map((e, i) => {
    const v = votes[i]
    return {
      upstreamId: e.upstreamId,
      ok: v?.status === 'completed',
      // voteID is chain-assigned on success; nullifier is seeded at job creation and readable before that.
      voteID: v?.voteID ?? v?.nullifier ?? '',
      error: v?.error ?? (v ? '' : 'No outcome reported for this question.'),
    }
  })
}
