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

  // Ballot state. `selected` holds the chosen choice indices (one for single-choice, several for
  // multichoice/approval).
  const [selected, setSelected] = useState([])
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

  // Toggle a choice. Single-choice replaces the selection; multichoice adds/removes it.
  const toggleChoice = (i) =>
    setSelected((s) => (info.allowMultiple ? (s.includes(i) ? s.filter((x) => x !== i) : [...s, i]) : [i]))

  async function submitVote(e) {
    e.preventDefault()
    setVoteErr('')
    setCasting(true)
    try {
      // Ballot encoding: single-choice = [chosenIndex]; approval = one 0/1 per choice.
      const choices = info.allowMultiple
        ? info.choices.map((_, i) => (selected.includes(i) ? 1 : 0))
        : [selected[0]]
      const id = await castVote({
        apiUrl: info.apiUrl,
        bundleId: info.bundleId,
        processId: info.processId,
        choices,
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
        <Results results={info.results} choices={info.choices} censusSize={info.censusSize} allowMultiple={info.allowMultiple} />
      ) : nullifier ? (
        <div className="vote-done">
          <strong>Your vote was cast.</strong>
          <p className="mono small muted">Nullifier {nullifier}</p>
        </div>
      ) : (
        <form onSubmit={submitVote}>
          <fieldset className="vote-choices">
            <legend className="eyebrow">{info.allowMultiple ? 'Choices (select one or more)' : 'Choices'}</legend>
            {info.choices.map((c, i) => (
              <label key={i} className="vote-choice">
                <input
                  type={info.allowMultiple ? 'checkbox' : 'radio'}
                  name="choice"
                  checked={selected.includes(i)}
                  onChange={() => toggleChoice(i)}
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
          <button disabled={casting || selected.length === 0 || !memberNumber.trim()}>
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

// Per-choice counts from the results histogram. Single-choice: results[0] is per-choice. Approval
// (multichoice): one field per choice, each [#voted-0, #voted-1] → the count is field[1].
function tallyCounts(results, allowMultiple) {
  if (!results || !results[0]) return null
  return allowMultiple
    ? results.map((field) => Number(field?.[1]) || 0)
    : results[0].map((v) => Number(v) || 0)
}

// Final tally, winning choice highlighted. Bars fill against the census size (eligible voters), so
// each shows turnout share; the winner is the choice with the most votes.
function Results({ results, choices, censusSize, allowMultiple }) {
  const nums = tallyCounts(results, allowMultiple)
  if (!nums) return <div className="vote-soon">Results are not available yet.</div>
  const max = Math.max(...nums, 0)
  if (max === 0) return <div className="vote-soon">No votes were cast.</div>
  const denom = censusSize > 0 ? censusSize : max
  return (
    <div className="tally vote-tally">
      {nums.map((n, ci) => {
        const win = n === max
        return (
          <div className={`bar${win ? ' win' : ''}`} key={ci}>
            <span className="bar-label">{choices?.[ci] ?? `Choice ${ci}`}</span>
            <span className="bar-track">
              <span className="bar-fill" style={{ width: `${Math.min(100, (n / denom) * 100)}%` }} />
            </span>
            <span className="bar-val">{n}</span>
          </div>
        )
      })}
    </div>
  )
}
