import { useEffect, useState } from 'react'
import { api } from '../api.js'

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

  useEffect(() => {
    ;(async () => {
      try {
        setInfo(await api(`/processes/${processId}`))
      } catch (e) {
        setError(e.message === 'HTTP 404' ? 'Voting process not found.' : e.message)
      } finally {
        setLoading(false)
      }
    })()
  }, [processId])

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
      </div>

      {done ? (
        <Results results={info.results} choices={info.choices} />
      ) : (
        <>
          <fieldset className="vote-choices" disabled>
            <legend className="eyebrow">Choices</legend>
            {info.choices.map((c, i) => (
              <label key={i} className="vote-choice">
                <input type="radio" name="choice" />
                <span>{c}</span>
              </label>
            ))}
          </fieldset>
          <div className="vote-soon">Voting from this page will be available soon.</div>
        </>
      )}

      <p className="mono small muted" style={{ marginTop: 14 }} title={info.processId}>
        Ref {info.processId.slice(0, 18)}…
      </p>
    </>,
  )
}

// Final tally, winning choice highlighted.
function Results({ results, choices }) {
  const row = results?.[0]
  if (!row) return <div className="vote-soon">Results are not available yet.</div>
  const nums = row.map((v) => Number(v) || 0)
  const max = Math.max(...nums, 0)
  if (max === 0) return <div className="vote-soon">No votes were cast.</div>
  return (
    <div className="tally vote-tally">
      {row.map((v, ci) => {
        const win = nums[ci] === max
        return (
          <div className={`bar${win ? ' win' : ''}`} key={ci}>
            <span className="bar-label">{choices?.[ci] ?? `Choice ${ci}`}</span>
            <span className="bar-track">
              <span className="bar-fill" style={{ width: `${(nums[ci] / max) * 100}%` }} />
            </span>
            <span className="bar-val">{v}</span>
          </div>
        )
      })}
    </div>
  )
}
