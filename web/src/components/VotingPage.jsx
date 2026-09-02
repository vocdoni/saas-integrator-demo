import { useState, useEffect } from 'react'
import { api } from '../api.js'
import { castProcessVotes } from '../voting.js'
import { isFinished } from '../status.js'
import QuestionResults from './QuestionResults.jsx'

const fmt = (s) => {
  try {
    return new Date(s).toLocaleString()
  } catch {
    return s
  }
}

// Build the on-chain ballot array for one question from its UI answer.
// single → [chosenIndex]; multiple → one 0/1 per option; ranked → unique rank per option (top = n-1).
function buildChoices(q, ans) {
  const n = q.choices.length
  if (q.kind === 'multiple') return q.choices.map((_, i) => (ans.selected.includes(i) ? 1 : 0))
  if (q.kind === 'ranked') {
    const ballot = new Array(n).fill(0)
    ans.order.forEach((optIdx, pos) => (ballot[optIdx] = n - 1 - pos))
    return ballot
  }
  return [ans.selected[0]]
}

const isAnswered = (q, ans) => (q.kind === 'ranked' ? true : (ans?.selected?.length ?? 0) > 0)
const blankAnswer = (q) => ({ selected: [], order: q.choices.map((_, i) => i), memo: '' })

// Did the voter pick the question's open "Other" choice (single-choice only, #577)?
const openSelected = (q, ans) => q.kind === 'single' && q.openChoiceIndex >= 0 && ans?.selected?.[0] === q.openChoiceIndex
// The memo is required once "Other" is picked.
const memoOk = (q, ans) => !openSelected(q, ans) || (ans?.memo?.trim().length ?? 0) > 0

export default function VotingPage({ processId }) {
  const [info, setInfo] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [answers, setAnswers] = useState({}) // questionId → { selected: number[], order: number[] }
  const [memberNumber, setMemberNumber] = useState('')
  const [casting, setCasting] = useState(false)
  const [voteErr, setVoteErr] = useState('')
  const [cast, setCast] = useState(null) // [{ upstreamId, voteID }] once cast

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

  useEffect(() => {
    if (info) setAnswers(Object.fromEntries(info.questions.map((q) => [q.id, blankAnswer(q)])))
  }, [info])

  // After casting, poll the live tally so new votes (incl. the block delay on the voter's own vote)
  // show up in real time. Stops once the process is finished (final) or the page unmounts.
  useEffect(() => {
    if (!cast || (info && isFinished(info))) return
    const id = setInterval(() => reload().catch(() => {}), 5000)
    return () => clearInterval(id)
  }, [cast, info])

  const setAns = (qid, patch) => setAnswers((a) => ({ ...a, [qid]: { ...a[qid], ...patch } }))
  const toggle = (q, i) => {
    const cur = answers[q.id]?.selected ?? []
    setAns(q.id, { selected: q.kind === 'multiple' ? (cur.includes(i) ? cur.filter((x) => x !== i) : [...cur, i]) : [i] })
  }
  const moveRank = (qid, pos, dir) => {
    const o = answers[qid]?.order ?? []
    const j = pos + dir
    if (j < 0 || j >= o.length) return
    const next = [...o]
    ;[next[pos], next[j]] = [next[j], next[pos]]
    setAns(qid, { order: next })
  }
  const dropRank = (qid, from, pos) => {
    if (from === null || from === pos) return
    const o = answers[qid]?.order ?? []
    const next = [...o]
    const [moved] = next.splice(from, 1)
    next.splice(pos, 0, moved)
    setAns(qid, { order: next })
  }

  async function submit(e) {
    e.preventDefault()
    setVoteErr('')
    setCasting(true)
    try {
      const payload = info.questions
        .filter((q) => q.upstreamId && isAnswered(q, answers[q.id]))
        .map((q) => ({
          upstreamId: q.upstreamId,
          choices: buildChoices(q, answers[q.id]),
          ...(openSelected(q, answers[q.id]) ? { memo: answers[q.id].memo.trim() } : {}),
        }))
      if (payload.length === 0) throw new Error('Answer at least one question.')
      const results = await castProcessVotes({
        apiUrl: info.apiUrl,
        processId: info.processId,
        chainId: info.chainId,
        memberNumber: memberNumber.trim(),
        answers: payload,
      })
      setCast(results)
      await reload()
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
  const statusText = done ? 'Closed' : info.status || ''
  const allAnswered = info.questions.every((q) => isAnswered(q, answers[q.id]) && memoOk(q, answers[q.id]))

  // The batch is relayed all or nothing, but each vote settles on chain on its own — so a job can
  // come back with some questions cast and some not. Report that split rather than implying success.
  const okCount = cast?.filter((o) => o.ok).length ?? 0
  const allCast = !!cast && okCount === cast.length
  const castHeading = allCast
    ? `Your vote${cast.length === 1 ? ' was' : 's were'} cast.`
    : okCount === 0
      ? 'Your votes could not be cast.'
      : `Only ${okCount} of ${cast.length} votes were cast.`

  return shell(
    <>
      <div className="vote-eyebrow">
        <span className="eyebrow">{done ? 'Voting closed' : 'Official ballot'}</span>
        <span className={`status s-${statusText.toLowerCase()}`}>{statusText}</span>
      </div>
      <h1>{info.title}</h1>
      {info.description && <p className="vote-desc">{info.description}</p>}

      <div className="vote-meta">
        <span>Opens <b>{fmt(info.startDate)}</b></span>
        <span>Closes <b>{fmt(info.endDate)}</b></span>
        <span><b className="num">{info.questions.length}</b> question{info.questions.length === 1 ? '' : 's'}</span>
      </div>

      {cast ? (
        <>
          <div className={allCast ? 'vote-done' : 'vote-done partial'}>
            <strong>{castHeading}</strong>
            {cast.map((o) => {
              const q = info.questions.find((x) => x.upstreamId === o.upstreamId)
              return (
                <div key={o.upstreamId} className="vote-outcome">
                  <span className="vo-title">{q?.title ?? o.upstreamId}</span>
                  {o.ok ? (
                    <span className="vo-ok">
                      <b aria-hidden="true">✓</b> cast <span className="mono small">{o.voteID}</span>
                    </span>
                  ) : (
                    <span className="vo-fail">
                      <b aria-hidden="true">✗</b> not cast — {o.error}
                    </span>
                  )}
                </div>
              )
            })}
          </div>
          <div className="results-list">
            {!done && <span className="eyebrow">Live results</span>}
            {info.questions.map((q) => <QuestionResults key={q.id} q={q} />)}
          </div>
        </>
      ) : done ? (
        <div className="results-list">
          {info.questions.map((q) => <QuestionResults key={q.id} q={q} />)}
        </div>
      ) : (
        <form onSubmit={submit}>
          {info.questions.map((q) => (
            <QuestionBallot
              key={q.id}
              q={q}
              ans={answers[q.id] ?? blankAnswer(q)}
              onToggle={(i) => toggle(q, i)}
              onMove={(pos, dir) => moveRank(q.id, pos, dir)}
              onDrop={(from, pos) => dropRank(q.id, from, pos)}
              onMemo={(v) => setAns(q.id, { memo: v })}
            />
          ))}

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
          <button disabled={casting || !memberNumber.trim() || !allAnswered}>
            {casting ? 'Casting vote…' : `Cast vote${info.questions.length === 1 ? '' : 's'}`}
          </button>
        </form>
      )}

      <p className="mono small muted" style={{ marginTop: 14 }}>Ref {info.processId}</p>
    </>,
  )
}

// One question's ballot: radios (single), checkboxes (multiple), or a drag-to-rank list (ranked).
function QuestionBallot({ q, ans, onToggle, onMove, onDrop, onMemo }) {
  const [dragIndex, setDragIndex] = useState(null)

  if (q.kind === 'ranked') {
    return (
      <fieldset className="vote-choices">
        <legend className="eyebrow">{q.title} — drag to rank (top = most preferred)</legend>
        {ans.order.map((optIdx, pos) => (
          <div
            key={optIdx}
            className={`vote-choice ranked${dragIndex === pos ? ' dragging' : ''}`}
            draggable
            onDragStart={() => setDragIndex(pos)}
            onDragOver={(e) => e.preventDefault()}
            onDrop={() => {
              onDrop(dragIndex, pos)
              setDragIndex(null)
            }}
            onDragEnd={() => setDragIndex(null)}
          >
            <span className="rank-badge">{pos + 1}</span>
            <span className="rank-label">{q.choices[optIdx]}</span>
            <span className="rank-moves">
              <button type="button" className="rank-move" disabled={pos === 0} onClick={() => onMove(pos, -1)} aria-label="Move up">▲</button>
              <button type="button" className="rank-move" disabled={pos === ans.order.length - 1} onClick={() => onMove(pos, 1)} aria-label="Move down">▼</button>
            </span>
            <span className="rank-grip" aria-hidden>⠿</span>
          </div>
        ))}
      </fieldset>
    )
  }

  const multiple = q.kind === 'multiple'
  // The open "Other" choice (single-choice only, #577): show a required free-text box when it's picked.
  const openPicked = q.kind === 'single' && q.openChoiceIndex >= 0 && ans.selected[0] === q.openChoiceIndex
  return (
    <fieldset className="vote-choices">
      <legend className="eyebrow">{q.title}{multiple ? ' (select one or more)' : ''}</legend>
      {q.choices.map((c, i) => (
        <label key={i} className="vote-choice">
          <input
            type={multiple ? 'checkbox' : 'radio'}
            name={`q-${q.id}`}
            checked={ans.selected.includes(i)}
            onChange={() => onToggle(i)}
          />
          <span>{c}</span>
        </label>
      ))}
      {openPicked && (
        <input
          className="memo-input"
          value={ans.memo}
          onChange={(e) => onMemo(e.target.value)}
          placeholder="Your answer (required)"
          maxLength={256}
          required
        />
      )}
    </fieldset>
  )
}
