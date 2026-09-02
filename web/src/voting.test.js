import { describe, it, expect, vi, beforeEach } from 'vitest'

// The SDK is mocked wholesale: these tests are about how castProcessVotes orchestrates the batch
// (one relay for the whole ballot, index-aligned outcomes, partial failure), not about Vocdoni crypto.
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
    sign: vi.fn(),
    voteBatch: vi.fn(),
    waitFor: vi.fn(),
    buildVoteTransaction: vi.fn(),
  }
})

vi.mock('@vocdoni/api-client', () => ({
  JobFailedError: h.JobFailedError,
  VocdoniApiClient: class {
    processes = { authStep0: h.authStep0, sign: h.sign }
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
}))

const { castProcessVotes } = await import('./voting.js')

const ANSWERS = [
  { upstreamId: 'q1', choices: [0] },
  { upstreamId: 'q2', choices: [1, 0] },
  { upstreamId: 'q3', choices: [2, 1, 0] },
]

const cast = (answers = ANSWERS) =>
  castProcessVotes({
    apiUrl: 'https://api.test',
    processId: 'p1',
    chainId: 'vocdoni-test-1',
    memberNumber: '42',
    answers,
  })

const completed = (votes) => ({ jobId: 'job1', type: 'relay_votes', status: 'completed', result: { votes } })

beforeEach(() => {
  vi.clearAllMocks()
  h.signerCount = 0
  h.authStep0.mockResolvedValue({ authToken: 'tok' })
  h.sign.mockResolvedValue({ signature: 'sig', weight: '0x1' })
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
  it('authenticates once and CSP-signs once per question', async () => {
    await cast()

    expect(h.authStep0).toHaveBeenCalledTimes(1)
    expect(h.authStep0).toHaveBeenCalledWith('p1', { memberNumber: '42' })
    expect(h.sign).toHaveBeenCalledTimes(3)
    expect(h.sign.mock.calls.map((c) => c[1].electionId)).toEqual(['q1', 'q2', 'q3'])
    // A fresh ephemeral identity per question, and the CSP signs that identity.
    expect(h.sign.mock.calls.map((c) => c[1].payload)).toEqual(['0xsigner1', '0xsigner2', '0xsigner3'])
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

  it('passes the chain id and CSP proof into every envelope', async () => {
    await cast()

    for (const [opts] of h.buildVoteTransaction.mock.calls) {
      expect(opts.chainId).toBe('vocdoni-test-1')
      expect(opts.cspSignature).toBe('sig')
      expect(opts.cspWeight).toBe('0x1')
    }
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
    h.waitFor.mockResolvedValue(completed([{ nullifier: 'n1', status: 'completed' }]))

    expect(await cast([ANSWERS[0]])).toEqual([{ upstreamId: 'q1', ok: true, voteID: 'n1', error: '' }])
  })

  it('does not produce undefined rows when the job reports no outcomes', async () => {
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
    expect(h.sign).not.toHaveBeenCalled()
    expect(h.voteBatch).not.toHaveBeenCalled()
  })
})
