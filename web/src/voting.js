import { VocdoniApiClient, JobFailedError } from '@vocdoni/api-client'
import { buildVoteTransaction, EphemeralSigner, signBlindCspBallots, ProofCA_Type } from '@vocdoni/api-voting'

// Casts ballots on a multi-question voting process (saas-backend #571) entirely through the SaaS API.
// The voter authenticates ONCE against the process census, CSP-signs the whole ballot in ONE call, builds
// one vote envelope per question (each question is its own on-chain election), then relays them all in a
// single batch (POST /votes, saas-backend #610) — one job covers the batch.
//
// Signing is the single fork between the two census kinds (both return the same
// [{ upstreamId, signature, weight, code?, error? }] shape, matched by upstreamId — never by index):
//  - regular census: POST /processes/{id}/sign-batch (saas-backend #634) — one CSP round trip.
//  - anonymous census (saas-backend #641, blind CSP): signBlindCspBallots() runs the two-round
//    blind-signature protocol (blind-point → blind → blind-sign → unblind) so the CSP never links the
//    voter to the ballot; the envelope then carries an ECDSA_BLIND_PIDSALTED proof. The CSP-pinned
//    `weight` must be passed back verbatim — it is hashed into the key the chain verifies against.
// A per-ballot sign failure (e.g. already_consumed) becomes a failed outcome row and the rest still
// relay. No automatic re-sign: a successful re-sign burns the election's finite overwrite budget.
//
// `answers` = [{ upstreamId, choices, memo? }] — `choices` is the on-chain ballot array for that
// question (single → [chosenIndex]; multiple → [v0..vN-1] (1 per pick); ranked → [v0..vN-1] unique
// ranks; cumulative → [v0..vN-1] credit per choice); `memo` is the optional free-text note attached
// when the open "Other" choice is picked (#577).
//
// Returns one outcome per answered question, in `answers` order:
//   [{ upstreamId, ok, voteID, error }]
// A batch rejected synchronously (bad envelope, unknown process, repeated nullifier, queue full)
// throws instead — nothing was relayed, so the caller can fix and retry safely.
export async function castProcessVotes({ apiUrl, processId, chainId, memberNumber, anonymous, answers }) {
  const client = new VocdoniApiClient({ apiUrl })

  // 1. Auth once per process (identical for anonymous). Auth-only census ⇒ the step-0 token is
  //    already verified (no OTP).
  const { authToken } = await client.processes.authStep0(processId, { memberNumber })
  if (!authToken) throw new Error('Authentication failed — check your member number.')

  // 2. One fresh ephemeral identity per question, created BEFORE signing: the address is inside the
  //    (possibly blinded) CA bundle, so the same signer must build that question's envelope.
  const signers = answers.map(() => new EphemeralSigner())
  const ballots = answers.map((a, i) => ({ upstreamId: a.upstreamId, address: signers[i].address }))

  // 3. Sign the whole ballot in one shot. NB: `processId` here is the SaaS process id, not an
  //    election id — both endpoints are process-scoped.
  const signatures = anonymous
    ? await signBlindCspBallots({ processId, authToken, client, ballots })
    : (await client.processes.signBatch(processId, { authToken, ballots })).signatures
  const byUpstream = new Map((signatures ?? []).map((s) => [s.upstreamId, s]))

  // 4. Build envelopes for the signed ballots; sign failures turn into outcome rows below.
  const envelopes = []
  const signFailures = new Map() // upstreamId → error text
  answers.forEach((a, i) => {
    const s = byUpstream.get(a.upstreamId)
    if (!s?.signature) {
      signFailures.set(a.upstreamId, s?.error || s?.code || 'The signer returned no signature for this question.')
      return
    }
    envelopes.push({
      upstreamId: a.upstreamId,
      // `memo` (free-text on the open choice, #577) rides in VoteEnvelope.memo; omitted when absent.
      txPayload: buildVoteTransaction({
        processId: a.upstreamId,
        choices: a.choices,
        chainId,
        signer: signers[i],
        cspSignature: s.signature,
        cspWeight: s.weight,
        ...(anonymous ? { proofType: ProofCA_Type.ECDSA_BLIND_PIDSALTED } : {}),
        ...(a.memo ? { memo: a.memo } : {}),
      }),
    })
  })

  // 5. Relay in one call — but only when something was signed. A process caps at 100 questions and a
  //    batch at 100 votes, so a full ballot always fits — no chunking needed.
  let votes = []
  if (envelopes.length > 0) {
    const { jobId } = await client.elections.voteBatch({
      votes: envelopes.map((e) => ({ txPayload: e.txPayload })),
    })

    // 6. One job covers the batch. It only completes when every envelope succeeded and fails otherwise,
    //    but a failed job still carries the per-envelope truth — so read the outcomes either way.
    let job
    try {
      job = await client.jobs.waitFor(jobId)
    } catch (e) {
      if (!(e instanceof JobFailedError)) throw e // network error or poll timeout: no outcomes to report
      job = e.job
    }
    votes = job.result?.votes ?? [] // index-aligned with the ENVELOPES (not the answers)
  }

  // Guard for a short/missing array so a malformed job can never yield undefined rows.
  const outcomeByUpstream = new Map(envelopes.map((e, i) => {
    const v = votes[i]
    return [e.upstreamId, {
      ok: v?.status === 'completed',
      // voteID is chain-assigned on success; nullifier is seeded at job creation and readable before that.
      voteID: v?.voteID ?? v?.nullifier ?? '',
      error: v?.error ?? (v ? '' : 'No outcome reported for this question.'),
    }]
  }))

  // One row per answered question, in answers order (same contract as always).
  return answers.map((a) =>
    signFailures.has(a.upstreamId)
      ? { upstreamId: a.upstreamId, ok: false, voteID: '', error: signFailures.get(a.upstreamId) }
      : { upstreamId: a.upstreamId, ...outcomeByUpstream.get(a.upstreamId) })
}
