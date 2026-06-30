import { useEffect, useState } from 'react'
import { api } from '../api.js'
import { castVote } from '../voting.js'
import { isFinished } from '../status.js'

const fmt = (s) => {
  try {
    return new Date(s).toLocaleString()
  } catch {
    return s
  }
}

export default function VotingPage({ processId }) {
  const [info, setInfo] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  // Ballot state. `selected` = chosen choice indices (single → one, multiple → several).
  // `order` = choice indices in the voter's ranked order (ranked only); `dragIndex` = row being dragged.
  const [selected, setSelected] = useState([])
  const [order, setOrder] = useState([])
  const [dragIndex, setDragIndex] = useState(null)
  const [memberNumber, setMemberNumber] = useState('')
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

  // Seed the ranked order to the choice order once the ballot loads.
  useEffect(() => {
    if (info?.votingType === 'ranked') setOrder(info.choices.map((_, i) => i))
  }, [info])

  // Single replaces the selection; multiple adds/removes.
  const toggleChoice = (i) =>
    setSelected((s) => (info.votingType === 'multiple' ? (s.includes(i) ? s.filter((x) => x !== i) : [...s, i]) : [i]))

  // Reorder the ranked list. `move` is the ▲/▼ (and touch) fallback; drag handlers do the same.
  const move = (pos, dir) =>
    setOrder((o) => {
      const j = pos + dir
      if (j < 0 || j >= o.length) return o
      const next = [...o]
      ;[next[pos], next[j]] = [next[j], next[pos]]
      return next
    })
  const drop = (pos) => {
    setOrder((o) => {
      if (dragIndex === null || dragIndex === pos) return o
      const next = [...o]
      const [moved] = next.splice(dragIndex, 1)
      next.splice(pos, 0, moved)
      return next
    })
    setDragIndex(null)
  }

  async function submitVote(e) {
    e.preventDefault()
    setVoteErr('')
    setCasting(true)
    try {
      const n = info.choices.length
      let choices
      if (info.votingType === 'multiple') {
        choices = info.choices.map((_, i) => (selected.includes(i) ? 1 : 0))
      } else if (info.votingType === 'ranked') {
        // Top of the list = most preferred = highest value (n-1); each option gets a unique rank.
        choices = new Array(n).fill(0)
        order.forEach((optIdx, pos) => (choices[optIdx] = n - 1 - pos))
      } else {
        choices = [selected[0]]
      }
      const id = await castVote({
        apiUrl: info.apiUrl,
        bundleId: info.bundleId,
        processId: info.processId,
        choices,
        memberNumber: memberNumber.trim(),
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
        <Results results={info.results} choices={info.choices} censusSize={info.censusSize} votingType={info.votingType} />
      ) : nullifier ? (
        <div className="vote-done">
          <strong>Your vote was cast.</strong>
          <p className="mono small muted">Nullifier {nullifier}</p>
        </div>
      ) : (
        <form onSubmit={submitVote}>
          {info.votingType === 'ranked' ? (
            <fieldset className="vote-choices">
              <legend className="eyebrow">Drag to rank — top is most preferred</legend>
              {order.map((optIdx, pos) => (
                <div
                  key={optIdx}
                  className={`vote-choice ranked${dragIndex === pos ? ' dragging' : ''}`}
                  draggable
                  onDragStart={() => setDragIndex(pos)}
                  onDragOver={(e) => e.preventDefault()}
                  onDrop={() => drop(pos)}
                  onDragEnd={() => setDragIndex(null)}
                >
                  <span className="rank-badge">{pos + 1}</span>
                  <span className="rank-label">{info.choices[optIdx]}</span>
                  <span className="rank-moves">
                    <button type="button" className="rank-move" disabled={pos === 0} onClick={() => move(pos, -1)} aria-label="Move up">▲</button>
                    <button type="button" className="rank-move" disabled={pos === order.length - 1} onClick={() => move(pos, 1)} aria-label="Move down">▼</button>
                  </span>
                  <span className="rank-grip" aria-hidden>⠿</span>
                </div>
              ))}
            </fieldset>
          ) : (
            <fieldset className="vote-choices">
              <legend className="eyebrow">{info.votingType === 'multiple' ? 'Choices (select one or more)' : 'Choices'}</legend>
              {info.choices.map((c, i) => (
                <label key={i} className="vote-choice">
                  <input
                    type={info.votingType === 'multiple' ? 'checkbox' : 'radio'}
                    name="choice"
                    checked={selected.includes(i)}
                    onChange={() => toggleChoice(i)}
                  />
                  <span>{c}</span>
                </label>
              ))}
            </fieldset>
          )}

          <label>
            Member number
            <input
              value={memberNumber}
              onChange={(e) => setMemberNumber(e.target.value)}
              placeholder="Your member number"
              required
            />
          </label>

          {voteErr && <div className="error">{voteErr}</div>}
          <button disabled={casting || !memberNumber.trim() || (info.votingType !== 'ranked' && selected.length === 0)}>
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

// Per-option numbers from the results histogram, by voting type:
// - single: results[0] is the per-choice count.
// - multiple (approval): one field per choice, each [#voted-0, #voted-1] → count is field[1].
// - ranked: one field per choice, a histogram over rank values → Borda score Σ count·value.
function tallyCounts(results, votingType) {
  if (!results || !results[0]) return null
  if (votingType === 'multiple') return results.map((f) => Number(f?.[1]) || 0)
  if (votingType === 'ranked') return results.map((f) => f.reduce((s, c, v) => s + (Number(c) || 0) * v, 0))
  return results[0].map((v) => Number(v) || 0)
}

// Final tally, winning choice highlighted. Single/multiple bars fill against the census size (turnout
// share); ranked shows a Borda score and fills against the top score. Winner = most votes/highest score.
function Results({ results, choices, censusSize, votingType }) {
  const nums = tallyCounts(results, votingType)
  if (!nums) return <div className="vote-soon">Results are not available yet.</div>
  const max = Math.max(...nums, 0)
  if (max === 0) return <div className="vote-soon">No votes were cast.</div>
  const denom = votingType === 'ranked' || !(censusSize > 0) ? max : censusSize
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
