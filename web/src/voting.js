import { VocdoniApiClient } from '@vocdoni/api-client'
import { VotingClient, EphemeralSigner } from '@vocdoni/api-voting'

// Casts a ballot entirely through the Vocdoni SaaS API via the integrator SDK — CSP auth, CSP sign,
// ballot build/encrypt, and relay. Never touches the chain directly. Returns the vote nullifier.
//
// processId is our stored 24-hex ProcessID (Mongo id); the SDK resolves the 64-hex on-chain address
// from it. bundleId/apiUrl come from GET /api/processes/{id}. `choices` is the on-chain ballot array:
// single → [chosenIndex]; multiple → [v0..vN-1] (1 per pick); ranked → [v0..vN-1] (unique rank values).
export async function castVote({ apiUrl, bundleId, processId, choices, memberNumber }) {
  const client = new VocdoniApiClient({ apiUrl })

  // Auth-only census: the step-0 token is already verified (no 2FA).
  const { authToken } = await client.bundle.authStep0(bundleId, { memberNumber })

  const election = await client.elections.get(processId)
  const onchainId = election.address // 64-hex Vochain id the CSP + vote envelope are keyed by

  const { hasVoted } = await client.bundle.check(bundleId, { authToken, electionId: onchainId })
  if (hasVoted) throw new Error('This member has already voted.')

  // Fresh ephemeral identity per vote: the CSP signs its address, the same key signs the vote tx.
  const signer = new EphemeralSigner()
  const { signature, weight } = await client.bundle.sign(
    bundleId, { authToken, electionId: onchainId, payload: signer.address })

  const jobId = await new VotingClient({ client }).vote({
    processId: onchainId,
    choices,
    chainId: election.chainId,
    signer,
    cspSignature: signature,
    cspWeight: weight,
    encryptionKeys: election.encryptionPublicKeys,
  })
  const job = await client.jobs.waitFor(jobId)
  return job.result?.voteID ?? jobId
}
