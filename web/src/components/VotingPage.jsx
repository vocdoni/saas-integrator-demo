import { useEffect, useState } from 'react'
import { api } from '../api.js'
import { castVote, bundleNeedsOtp } from '../voting.js'

const fmt = (s) => {
  try {
    return new Date(s).toLocaleString()
  } catch {
    return s
  }
}

// Has voting ended? on-chain RESULTS/ENDED, owner-closed, or past the end date.
function isFinished(info) {
  const oc = (info.onchainStatus || '').toUpperCase()
  if (oc === 'RESULTS' || oc === 'ENDED' || oc === 'CANCELED') return true
  if (info.status === 'Closed') return true
  if (info.endDate && new Date(info.endDate).getTime() < Date.now()) return true
  return false
}

export default function VotingPage({ processId }) {
  const [info, setInfo] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  // Ballot state.
  const [selected, setSelected] = useState(null)
  const [memberNumber, setMemberNumber] = useState('')
  const [otp, setOtp] = useState('')
  const [needsOtp, setNeedsOtp] = useState(false)
  const [casting, setCasting] = useState(false)
  const [voteErr, setVoteErr] = useState('')
  const [nullifier, setNullifier] = useState('')

  const reload = async () => setInfo(await api(`/processes/${processId}`))

  useEffect(() => {
    ;(async () => {
      try {
        await reload()
      } catch (e) {
        setError(e.message === 'HTTP 404' ? 'Voting process not found.' : e.message)
      } finally {
        setLoading(false)
      }
    })()
  }, [processId])

  // While voting is open, ask the bundle whether it needs a 2FA OTP (auth-only census → no field).
  useEffect(() => {
    if (!info || isFinished(info)) return
    bundleNeedsOtp({ apiUrl: info.apiUrl, bundleId: info.bundleId })
      .then(setNeedsOtp)
      .catch(() => setNeedsOtp(false))
  }, [info])

  async function submitVote(e) {
    e.preventDefault()
    setVoteErr('')
    setCasting(true)
    try {
      const id = await castVote({
        apiUrl: info.apiUrl,
        bundleId: info.bundleId,
        processId: info.processId,
        choices: [selected],
        memberNumber: memberNumber.trim(),
        otp: otp.trim(),
      })
      setNullifier(id)
      await reload() // refresh the vote count
    } catch (e) {
      setVoteErr(e.message || 'Could not cast the vote.')
    } finally {
      setCasting(false)
    }
  }

  const shell = (children) => (
    <div className="voting">
      <div className="card vote-card">
        <div className="vote-rule" />
        <div className="vote-body">{children}</div>
      </div>
    </div>
  )

  if (loading) return shell(<p className="muted">Loading ballot…</p>)
  if (error) return shell(<div className="error">{error}</div>)

  const done = isFinished(info)
  const rawStatus = info.onchainStatus || info.status || ''
  const statusText = done && /ready|open/i.test(rawStatus) ? 'Closed' : rawStatus

  return shell(
    <>
      <div className="vote-eyebrow">
        <span className="eyebrow">{done ? 'Final results' : 'Official ballot'}</span>
        <span className={`status s-${statusText.toLowerCase()}`}>{statusText}</span>
      </div>
      <h1>{info.title}</h1>
      {info.description && <p className="vote-desc">{info.description}</p>}

      <div className="vote-meta">
        <span>Opens <b>{fmt(info.startDate)}</b></span>
        <span>Closes <b>{fmt(info.endDate)}</b></span>
        {info.voteCount != null && (
          <span><b className="num">{info.voteCount}</b> votes cast</span>
        )}
        {info.censusSize != null && (
          <span><b className="num">{info.censusSize}</b> eligible</span>
        )}
      </div>

      {done ? (
        <Results results={info.results} choices={info.choices} censusSize={info.censusSize} />
      ) : nullifier ? (
        <div className="vote-done">
          <strong>Your vote was cast.</strong>
          <p className="mono small muted">Nullifier {nullifier}</p>
        </div>
      ) : (
        <form onSubmit={submitVote}>
          <fieldset className="vote-choices">
            <legend className="eyebrow">Choices</legend>
            {info.choices.map((c, i) => (
              <label key={i} className="vote-choice">
                <input
                  type="radio"
                  name="choice"
                  checked={selected === i}
                  onChange={() => setSelected(i)}
                />
                <span>{c}</span>
              </label>
            ))}
          </fieldset>

          <label>
            Member number
            <input
              value={memberNumber}
              onChange={(e) => setMemberNumber(e.target.value)}
              placeholder="Your member number"
              required
            />
          </label>
          {needsOtp && (
            <label>
              Email code
              <input
                value={otp}
                onChange={(e) => setOtp(e.target.value)}
                placeholder="One-time code sent to your email"
                required
              />
            </label>
          )}

          {voteErr && <div className="error">{voteErr}</div>}
          <button disabled={casting || selected === null || !memberNumber.trim()}>
            {casting ? 'Casting vote…' : 'Cast vote'}
          </button>
        </form>
      )}

      <p className="mono small muted" style={{ marginTop: 14 }}>
        Ref {info.processId}
      </p>
    </>,
  )
}

// Final tally, winning choice highlighted. Bars fill against the census size (eligible voters), so
// each shows turnout share; the winner is still the choice with the most votes. Falls back to the
// leading tally if the census size is unavailable.
function Results({ results, choices, censusSize }) {
  const row = results?.[0]
  if (!row) return <div className="vote-soon">Results are not available yet.</div>
  const nums = row.map((v) => Number(v) || 0)
  const max = Math.max(...nums, 0)
  if (max === 0) return <div className="vote-soon">No votes were cast.</div>
  const denom = censusSize > 0 ? censusSize : max
  return (
    <div className="tally vote-tally">
      {row.map((v, ci) => {
        const win = nums[ci] === max
        return (
          <div className={`bar${win ? ' win' : ''}`} key={ci}>
            <span className="bar-label">{choices?.[ci] ?? `Choice ${ci}`}</span>
            <span className="bar-track">
              <span className="bar-fill" style={{ width: `${Math.min(100, (nums[ci] / denom) * 100)}%` }} />
            </span>
            <span className="bar-val">{v}</span>
          </div>
        )
      })}
    </div>
  )
}
