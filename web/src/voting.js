import { VocdoniApiClient } from '@vocdoni/api-client'
import { VotingClient, EphemeralSigner } from '@vocdoni/api-voting'

// Casts a ballot entirely through the Vocdoni SaaS API via the integrator SDK — CSP auth, CSP sign,
// ballot build/encrypt, and relay. Never touches the chain directly. Returns the vote nullifier.
//
// processId is our stored 24-hex ProcessID (Mongo id); the SDK resolves the 64-hex on-chain address
// from it. bundleId/apiUrl come from GET /api/processes/{id}. choices is one value per question
// (single yes/no question → [selectedChoiceIndex]).
// Does this bundle's census require a 2FA OTP (vs auth-only by member number)? Drives whether the
// voting page shows an OTP field. Best-effort: callers treat a thrown error as "assume no OTP".
export async function bundleNeedsOtp({ apiUrl, bundleId }) {
  const bundle = await new VocdoniApiClient({ apiUrl }).bundle.get(bundleId)
  return (bundle.census?.twoFaFields?.length ?? 0) > 0
}

export async function castVote({ apiUrl, bundleId, processId, choices, memberNumber, otp }) {
  const client = new VocdoniApiClient({ apiUrl })

  const bundle = await client.bundle.get(bundleId)
  // Auth-only census (no twoFaFields): the step-0 token is already verified. With 2FA, confirm the OTP.
  let { authToken } = await client.bundle.authStep0(bundleId, { memberNumber })
  if ((bundle.census?.twoFaFields?.length ?? 0) > 0)
    ({ authToken } = await client.bundle.authStep1(bundleId, { authToken, authData: [otp] }))

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
