import { describe, it, expect, vi, beforeEach } from 'vitest'

// The SDK is mocked wholesale: these tests are about how castProcessVotes orchestrates the flow
// (one batch sign — plain or blind — one relay for the whole ballot, upstreamId matching, partial
// failure), not about Vocdoni crypto.
const h = vi.hoisted(() => {
  // Must be a real class — voting.js distinguishes a failed job from a transport error with instanceof.
  class JobFailedError extends Error {
    constructor(job) {
      super('job failed')
      this.job = job
    }
  }
  return {
    JobFailedError,
    signerCount: 0,
    authStep0: vi.fn(),
    signBatch: vi.fn(),
    signBlindCspBallots: vi.fn(),
    voteBatch: vi.fn(),
    waitFor: vi.fn(),
    buildVoteTransaction: vi.fn(),
  }
})

vi.mock('@vocdoni/api-client', () => ({
  JobFailedError: h.JobFailedError,
  VocdoniApiClient: class {
    processes = { authStep0: h.authStep0, signBatch: h.signBatch }
    elections = { voteBatch: h.voteBatch }
    jobs = { waitFor: h.waitFor }
  },
}))

vi.mock('@vocdoni/api-voting', () => ({
  EphemeralSigner: class {
    constructor() {
      this.address = `0xsigner${++h.signerCount}`
    }
  },
  buildVoteTransaction: h.buildVoteTransaction,
  signBlindCspBallots: h.signBlindCspBallots,
  // Opaque token — the code must pass it through as-is, so identity is what matters.
  ProofCA_Type: { ECDSA_BLIND_PIDSALTED: 'blind-proof-type' },
}))

const { castProcessVotes } = await import('./voting.js')

const ANSWERS = [
  { upstreamId: 'q1', choices: [0] },
  { upstreamId: 'q2', choices: [1, 0] },
  { upstreamId: 'q3', choices: [2, 1, 0] },
]

const SIGNED = [
  { upstreamId: 'q1', signature: 'sig1', weight: '0x1' },
  { upstreamId: 'q2', signature: 'sig2', weight: '0x2' },
  { upstreamId: 'q3', signature: 'sig3', weight: '0x3' },
]

const cast = (answers = ANSWERS, { anonymous = false } = {}) =>
  castProcessVotes({
    apiUrl: 'https://api.test',
    processId: 'p1',
    chainId: 'vocdoni-test-1',
    memberNumber: '42',
    anonymous,
    answers,
  })

const completed = (votes) => ({ jobId: 'job1', type: 'relay_votes', status: 'completed', result: { votes } })

beforeEach(() => {
  vi.clearAllMocks()
  h.signerCount = 0
  h.authStep0.mockResolvedValue({ authToken: 'tok' })
  h.signBatch.mockResolvedValue({ signatures: SIGNED })
  h.signBlindCspBallots.mockResolvedValue(SIGNED)
  h.buildVoteTransaction.mockImplementation((o) => `tx-${o.processId}`)
  h.voteBatch.mockResolvedValue({ jobId: 'job1' })
  h.waitFor.mockResolvedValue(
    completed([
      { processId: 'q1', nullifier: 'n1', voteID: 'v1', status: 'completed' },
      { processId: 'q2', nullifier: 'n2', voteID: 'v2', status: 'completed' },
      { processId: 'q3', nullifier: 'n3', voteID: 'v3', status: 'completed' },
    ]),
  )
})

describe('castProcessVotes', () => {
  it('authenticates once and signs the whole ballot in one batch call', async () => {
    await cast()

    expect(h.authStep0).toHaveBeenCalledTimes(1)
    expect(h.authStep0).toHaveBeenCalledWith('p1', { memberNumber: '42' })
    // One CSP round trip for the whole ballot (saas-backend #634), with a fresh ephemeral
    // identity per question, created before signing.
    expect(h.signBatch).toHaveBeenCalledTimes(1)
    expect(h.signBatch).toHaveBeenCalledWith('p1', {
      authToken: 'tok',
      ballots: [
        { upstreamId: 'q1', address: '0xsigner1' },
        { upstreamId: 'q2', address: '0xsigner2' },
        { upstreamId: 'q3', address: '0xsigner3' },
      ],
    })
    expect(h.signBlindCspBallots).not.toHaveBeenCalled()
  })

  it('relays the whole ballot in exactly one batch, in answers order', async () => {
    await cast()

    expect(h.buildVoteTransaction).toHaveBeenCalledTimes(3)
    expect(h.buildVoteTransaction.mock.calls.map((c) => c[0].processId)).toEqual(['q1', 'q2', 'q3'])
    expect(h.buildVoteTransaction.mock.calls.map((c) => c[0].choices)).toEqual([[0], [1, 0], [2, 1, 0]])

    // The point of the change: one relay for the ballot, so a failure can never half-vote it.
    expect(h.voteBatch).toHaveBeenCalledTimes(1)
    expect(h.voteBatch).toHaveBeenCalledWith({
      votes: [{ txPayload: 'tx-q1' }, { txPayload: 'tx-q2' }, { txPayload: 'tx-q3' }],
    })
    expect(h.waitFor).toHaveBeenCalledTimes(1)
    expect(h.waitFor).toHaveBeenCalledWith('job1')
  })

  it('passes the chain id and each ballot’s own CSP proof into its envelope', async () => {
    await cast()

    for (const [opts] of h.buildVoteTransaction.mock.calls) {
      expect(opts.chainId).toBe('vocdoni-test-1')
      // No proofType on the plain path — buildVoteTransaction defaults to ECDSA_PIDSALTED.
      expect(opts.proofType).toBeUndefined()
    }
    // Per-ballot signature + weight verbatim from the matching batch entry.
    expect(h.buildVoteTransaction.mock.calls.map((c) => [c[0].cspSignature, c[0].cspWeight])).toEqual([
      ['sig1', '0x1'],
      ['sig2', '0x2'],
      ['sig3', '0x3'],
    ])
  })

  it('matches signatures by upstreamId, not by position', async () => {
    // A shuffled response must still put each signature on its own question's envelope.
    h.signBatch.mockResolvedValue({ signatures: [SIGNED[2], SIGNED[0], SIGNED[1]] })

    await cast()

    expect(h.buildVoteTransaction.mock.calls.map((c) => [c[0].processId, c[0].cspSignature])).toEqual([
      ['q1', 'sig1'],
      ['q2', 'sig2'],
      ['q3', 'sig3'],
    ])
  })

  it('reports a per-ballot sign failure as a failed row and still relays the rest', async () => {
    h.signBatch.mockResolvedValue({
      signatures: [SIGNED[0], { upstreamId: 'q2', code: 'already_consumed', error: 'signature already consumed' }, SIGNED[2]],
    })
    h.waitFor.mockResolvedValue(
      completed([
        { processId: 'q1', voteID: 'v1', status: 'completed' },
        { processId: 'q3', voteID: 'v3', status: 'completed' },
      ]),
    )

    expect(await cast()).toEqual([
      { upstreamId: 'q1', ok: true, voteID: 'v1', error: '' },
      { upstreamId: 'q2', ok: false, voteID: '', error: 'signature already consumed' },
      { upstreamId: 'q3', ok: true, voteID: 'v3', error: '' },
    ])
    // Only the signed ballots were relayed — and the job's outcomes align with the envelopes.
    expect(h.voteBatch).toHaveBeenCalledWith({ votes: [{ txPayload: 'tx-q1' }, { txPayload: 'tx-q3' }] })
  })

  it('skips the relay entirely when no ballot was signed, without retrying the sign', async () => {
    // No automatic re-sign, even for a retryable code — a successful re-sign burns overwrite budget.
    h.signBatch.mockResolvedValue({
      signatures: ANSWERS.map((a) => ({ upstreamId: a.upstreamId, code: 'already_signing', error: 'busy' })),
    })

    expect(await cast()).toEqual(ANSWERS.map((a) => ({ upstreamId: a.upstreamId, ok: false, voteID: '', error: 'busy' })))
    expect(h.signBatch).toHaveBeenCalledTimes(1)
    expect(h.voteBatch).not.toHaveBeenCalled()
    expect(h.waitFor).not.toHaveBeenCalled()
  })

  it('anonymous: blind-signs via signBlindCspBallots and never touches the plain CSP endpoints', async () => {
    await cast(ANSWERS, { anonymous: true })

    expect(h.signBlindCspBallots).toHaveBeenCalledTimes(1)
    // NB: the SaaS process id, not an election id — the blind endpoints are process-scoped.
    expect(h.signBlindCspBallots).toHaveBeenCalledWith({
      processId: 'p1',
      authToken: 'tok',
      client: expect.anything(),
      ballots: [
        { upstreamId: 'q1', address: '0xsigner1' },
        { upstreamId: 'q2', address: '0xsigner2' },
        { upstreamId: 'q3', address: '0xsigner3' },
      ],
    })
    expect(h.signBatch).not.toHaveBeenCalled()
  })

  it('anonymous: stamps the blind proof type and the verbatim weight on every envelope', async () => {
    await cast(ANSWERS, { anonymous: true })

    for (const [opts] of h.buildVoteTransaction.mock.calls) {
      expect(opts.proofType).toBe('blind-proof-type')
    }
    expect(h.buildVoteTransaction.mock.calls.map((c) => c[0].cspWeight)).toEqual(['0x1', '0x2', '0x3'])
  })

  it('maps a completed job onto per-question outcomes, index-aligned', async () => {
    expect(await cast()).toEqual([
      { upstreamId: 'q1', ok: true, voteID: 'v1', error: '' },
      { upstreamId: 'q2', ok: true, voteID: 'v2', error: '' },
      { upstreamId: 'q3', ok: true, voteID: 'v3', error: '' },
    ])
  })

  it('reports a partial failure instead of throwing', async () => {
    // A relay_votes job fails if any one envelope fails, but still carries the per-vote truth.
    h.waitFor.mockRejectedValue(
      new h.JobFailedError({
        jobId: 'job1',
        type: 'relay_votes',
        status: 'failed',
        errors: ['vote 2: vote already exists'],
        result: {
          votes: [
            { processId: 'q1', nullifier: 'n1', voteID: 'v1', status: 'completed' },
            { processId: 'q2', nullifier: 'n2', voteID: 'v2', status: 'completed' },
            { processId: 'q3', nullifier: 'n3', status: 'failed', error: 'vote already exists' },
          ],
        },
      }),
    )

    expect(await cast()).toEqual([
      { upstreamId: 'q1', ok: true, voteID: 'v1', error: '' },
      { upstreamId: 'q2', ok: true, voteID: 'v2', error: '' },
      { upstreamId: 'q3', ok: false, voteID: 'n3', error: 'vote already exists' },
    ])
  })

  it('falls back to the nullifier when the chain has not assigned a voteID yet', async () => {
    h.signBatch.mockResolvedValue({ signatures: [SIGNED[0]] })
    h.waitFor.mockResolvedValue(completed([{ nullifier: 'n1', status: 'completed' }]))

    expect(await cast([ANSWERS[0]])).toEqual([{ upstreamId: 'q1', ok: true, voteID: 'n1', error: '' }])
  })

  it('does not produce undefined rows when the job reports no outcomes', async () => {
    h.signBatch.mockResolvedValue({ signatures: [SIGNED[0]] })
    h.waitFor.mockResolvedValue({ jobId: 'job1', type: 'relay_votes', status: 'completed', result: {} })

    expect(await cast([ANSWERS[0]])).toEqual([
      { upstreamId: 'q1', ok: false, voteID: '', error: 'No outcome reported for this question.' },
    ])
  })

  it('propagates a transport failure from the job poll', async () => {
    h.waitFor.mockRejectedValue(new Error('Timed out waiting for job job1 after 60000ms'))

    await expect(cast()).rejects.toThrow('Timed out')
  })

  it('propagates a synchronously rejected batch without polling a job', async () => {
    // The backend validates the batch as a unit: a 4xx here means nothing was relayed.
    h.voteBatch.mockRejectedValue(new Error('vote index 2 repeats the nullifier of vote index 0'))

    await expect(cast()).rejects.toThrow('repeats the nullifier')
    expect(h.waitFor).not.toHaveBeenCalled()
  })

  it('fails before signing anything when authentication is rejected', async () => {
    h.authStep0.mockResolvedValue({})

    await expect(cast()).rejects.toThrow('check your member number')
    expect(h.signBatch).not.toHaveBeenCalled()
    expect(h.voteBatch).not.toHaveBeenCalled()
  })
})
