import { VocdoniApiClient } from '@vocdoni/api-client'
import { VotingClient, EphemeralSigner } from '@vocdoni/api-voting'

// Casts ballots on a multi-question voting process (saas-backend #571) entirely through the SaaS API.
// The voter authenticates ONCE against the process census, then signs + relays ONE vote per question
// (each question is its own on-chain election). Returns [{ upstreamId, voteID }] per answered question.
//
// `answers` = [{ upstreamId, choices }] — `choices` is the on-chain ballot array for that question:
// single → [chosenIndex]; multiple → [v0..vN-1] (1 per pick); ranked → [v0..vN-1] (unique rank values).
export async function castProcessVotes({ apiUrl, processId, chainId, memberNumber, answers }) {
  const client = new VocdoniApiClient({ apiUrl })

  // 1. Auth once per process. Auth-only census ⇒ the step-0 token is already verified (no OTP).
  const { authToken } = await cspPost(apiUrl, `/processes/${processId}/auth/0`, { memberNumber })
  if (!authToken) throw new Error('Authentication failed — check your member number.')

  // 2. Per answered question: CSP-sign a fresh ephemeral identity for that election, then build + relay.
  const voting = new VotingClient({ client })
  const results = []
  for (const { upstreamId, choices } of answers) {
    const signer = new EphemeralSigner()
    const { signature, weight } = await cspPost(apiUrl, `/processes/${processId}/sign`, {
      authToken,
      payload: signer.address,
      electionId: upstreamId, // the target question's on-chain election id
    })
    // VotingClient.vote builds/encodes the vote tx and relays it via POST /vote (unchanged endpoint).
    const jobId = await voting.vote({
      processId: upstreamId,
      choices,
      chainId,
      signer,
      cspSignature: signature,
      cspWeight: weight,
    })
    const job = await client.jobs.waitFor(jobId)
    results.push({ upstreamId, voteID: job.result?.voteID ?? jobId })
  }
  return results
}

// The #571 CSP voter endpoints (/processes/{id}/auth|check|sign) aren't in @vocdoni/api-client, so
// call them with raw fetch. Public endpoints — no auth header.
async function cspPost(apiUrl, path, body) {
  const res = await fetch(`${apiUrl.replace(/\/$/, '')}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!res.ok) throw new Error(`${path} → HTTP ${res.status}: ${(await res.text()).slice(0, 200)}`)
  return res.json()
}
