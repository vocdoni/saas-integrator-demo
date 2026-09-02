import { useEffect, useState } from 'react'
import { api } from '../api.js'
import { isFinished } from '../status.js'
import DangerZone from './DangerZone.jsx'
import QuestionResults from './QuestionResults.jsx'

const pad = (n) => String(n).padStart(2, '0')
const toLocalInput = (d) =>
  `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
const short = (s) => (s ? `${s.slice(0, 10)}…${s.slice(-6)}` : '')
const ONE_DAY = 864e5
const plusDay = (localStr) => toLocalInput(new Date(new Date(localStr).getTime() + ONE_DAY))

// openIndex = the choice marked as the free-text "Other" option (#577), or -1. Single-choice only.
// budget/costExponent apply to cumulative questions only (costExponent 2 = quadratic).
const blankQuestion = () => ({ title: '', choices: ['Yes', 'No'], kind: 'single', openIndex: -1, budget: 10, costExponent: 1 })
const blankForm = () => {
  const start = toLocalInput(new Date())
  return { title: '', description: '', startDate: start, endDate: plusDay(start), questions: [blankQuestion()], anonymous: false }
}

const KINDS = [
  ['single', 'Single choice'],
  ['multiple', 'Multiple choice'],
  ['ranked', 'Ranked voting'],
  ['cumulative', 'Cumulative voting'],
]

export default function Proposals({ assoc }) {
  const [items, setItems] = useState([])
  const [loading, setLoading] = useState(true)
  const [form, setForm] = useState(blankForm())
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [closing, setClosing] = useState(() => new Set()) // proposal ids with a pending close
  // Anonymous voting is plan-gated upstream and publish fails opaquely without it — fetch the flag
  // (best-effort: an unreachable read just leaves the toggle disabled).
  const [anonymousAllowed, setAnonymousAllowed] = useState(false)

  const base = `/associations/${assoc.id}/proposals`

  async function load() {
    setLoading(true)
    try {
      setItems(await api(base))
    } catch (e) {
      setError(e.message)
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => {
    load()
    api(`/associations/${assoc.id}/features`)
      .then((f) => setAnonymousAllowed(!!f?.anonymousVoting))
      .catch(() => setAnonymousAllowed(false))
  }, [assoc.id])

  // Question + choice editing (functional updates — no stale closures).
  const updateQ = (qi, fn) => setForm((f) => ({ ...f, questions: f.questions.map((q, i) => (i === qi ? fn(q) : q)) }))
  const setQuestion = (qi, patch) => updateQ(qi, (q) => ({ ...q, ...patch }))
  const addQuestion = () => setForm((f) => ({ ...f, questions: [...f.questions, blankQuestion()] }))
  const removeQuestion = (qi) => setForm((f) => ({ ...f, questions: f.questions.filter((_, i) => i !== qi) }))
  const setChoice = (qi, ci, v) => updateQ(qi, (q) => ({ ...q, choices: q.choices.map((c, j) => (j === ci ? v : c)) }))
  const addChoice = (qi) => updateQ(qi, (q) => ({ ...q, choices: [...q.choices, ''] }))
  // Removing a choice shifts indices — keep openIndex pointing at the same choice (or clear it).
  const removeChoice = (qi, ci) =>
    updateQ(qi, (q) => ({
      ...q,
      choices: q.choices.filter((_, j) => j !== ci),
      openIndex: ci === q.openIndex ? -1 : ci < q.openIndex ? q.openIndex - 1 : q.openIndex,
    }))
  // Kind selector: an open "Other" choice is single-choice only, so clear it when leaving single.
  const setKind = (qi, kind) => updateQ(qi, (q) => ({ ...q, kind, openIndex: kind === 'single' ? q.openIndex : -1 }))
  const toggleOpen = (qi, ci) => updateQ(qi, (q) => ({ ...q, openIndex: q.openIndex === ci ? -1 : ci }))

  async function create(e) {
    e.preventDefault()
    setError('')
    setBusy(true)
    try {
      const questions = form.questions.map((q) => ({
        title: q.title.trim(),
        kind: q.kind,
        // Carry the "open" flag on each choice, then drop empties (open rides the surviving object).
        choices: q.choices
          .map((title, i) => ({ title: title.trim(), open: q.kind === 'single' && i === q.openIndex }))
          .filter((c) => c.title),
        ...(q.kind === 'cumulative' ? { budget: Number(q.budget), costExponent: Number(q.costExponent) } : {}),
      }))
      if (questions.some((q) => !q.title)) throw new Error('Every question needs a title.')
      if (questions.some((q) => q.choices.length < 2)) throw new Error('Every question needs at least two choices.')
      if (questions.some((q) => q.kind === 'cumulative' && !(q.budget > 0))) throw new Error('Cumulative questions need a budget of at least 1.')
      await api(base, {
        method: 'POST',
        body: {
          title: form.title,
          description: form.description,
          startDate: new Date(form.startDate).toISOString(),
          endDate: new Date(form.endDate).toISOString(),
          questions,
          anonymous: form.anonymous,
        },
      })
      setForm(blankForm())
      await load()
    } catch (e) {
      setError(e.message)
    } finally {
      setBusy(false)
    }
  }

  const unmarkClosing = (pid) =>
    setClosing((s) => {
      const n = new Set(s)
      n.delete(pid)
      return n
    })

  // The close endpoint enqueues the on-chain end and returns immediately; the real status only flips
  // to Closed once the end tx mines. Show a "Closing…" badge and auto-refresh until it does.
  async function close(pid) {
    if (!confirm('Close voting on this process (ends every question)?')) return
    setError('')
    setClosing((s) => new Set(s).add(pid))
    try {
      await api(`${base}/${pid}/close`, { method: 'POST' })
    } catch (e) {
      setError(e.message)
      unmarkClosing(pid)
      return
    }
    for (let i = 0; i < 20; i++) {
      await new Promise((r) => setTimeout(r, 3000))
      let fresh
      try {
        fresh = await api(base) // re-fetch without load()'s full-list "Loading…" flicker
      } catch {
        continue
      }
      setItems(fresh)
      const p = fresh.find((x) => x.id === pid)
      if (!p || p.status === 'Closed' || isFinished(p)) break
    }
    unmarkClosing(pid)
  }

  return (
    <div className="grid">
      <section className="card">
        <h3>New voting process</h3>
        <form onSubmit={create}>
          <label>
            Title
            <input value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} required />
          </label>
          <label>
            Description
            <textarea value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </label>

          <div className="row2">
            <label>
              Start
              <input
                type="datetime-local"
                value={form.startDate}
                onChange={(e) => setForm({ ...form, startDate: e.target.value, endDate: plusDay(e.target.value) })}
                required
              />
            </label>
            <label>
              End
              <input type="datetime-local" value={form.endDate} onChange={(e) => setForm({ ...form, endDate: e.target.value })} required />
            </label>
          </div>

          <div className="questions">
            <span className="label">Questions</span>
            {form.questions.map((q, qi) => (
              <div key={qi} className="question-editor">
                <div className="qe-head">
                  <span className="label small">Question {qi + 1}</span>
                  {form.questions.length > 1 && (
                    <button type="button" className="link danger" onClick={() => removeQuestion(qi)}>Remove</button>
                  )}
                </div>
                <label>
                  <input value={q.title} onChange={(e) => setQuestion(qi, { title: e.target.value })} placeholder="Question" required />
                </label>
                <div className="choices">
                  {q.choices.map((c, ci) => (
                    <div key={ci} className="choice-row">
                      <input value={c} onChange={(e) => setChoice(qi, ci, e.target.value)} placeholder={`Choice ${ci + 1}`} />
                      {q.kind === 'single' && (
                        <label className="check small" title="Voters who pick this choice attach a free-text answer">
                          <input type="checkbox" checked={q.openIndex === ci} onChange={() => toggleOpen(qi, ci)} />
                          <span>Other</span>
                        </label>
                      )}
                      {q.choices.length > 2 && (
                        <button type="button" className="link danger" onClick={() => removeChoice(qi, ci)}>×</button>
                      )}
                    </div>
                  ))}
                  <button type="button" className="link" onClick={() => addChoice(qi)}>+ Add choice</button>
                </div>
                <fieldset className="vote-type">
                  {KINDS.map(([value, title]) => (
                    <label key={value} className="check">
                      <input type="radio" name={`kind-${qi}`} checked={q.kind === value} onChange={() => setKind(qi, value)} />
                      <span>{title}</span>
                    </label>
                  ))}
                </fieldset>
                {q.kind === 'cumulative' && (
                  <div className="cumulative-setup">
                    <label className="check small">
                      Budget
                      <input
                        type="number"
                        className="alloc-input"
                        min={1}
                        step={1}
                        value={q.budget}
                        onChange={(e) => setQuestion(qi, { budget: e.target.value })}
                        required
                      />
                    </label>
                    <label className="check small">
                      <input type="radio" name={`cost-${qi}`} checked={q.costExponent === 1} onChange={() => setQuestion(qi, { costExponent: 1 })} />
                      <span>Linear</span>
                    </label>
                    <label className="check small" title="v credits on one choice cost v² budget">
                      <input type="radio" name={`cost-${qi}`} checked={q.costExponent === 2} onChange={() => setQuestion(qi, { costExponent: 2 })} />
                      <span>Quadratic</span>
                    </label>
                  </div>
                )}
              </div>
            ))}
            <button type="button" className="link" onClick={addQuestion}>+ Add question</button>
          </div>

          <label
            className="check"
            title={anonymousAllowed
              ? 'Blind-signature voting: the census authority cannot link voters to ballots.'
              : 'Anonymous voting is not included in this organization’s plan.'}
          >
            <input
              type="checkbox"
              checked={form.anonymous}
              disabled={!anonymousAllowed}
              onChange={(e) => setForm({ ...form, anonymous: e.target.checked })}
            />
            <span>Anonymous voting{anonymousAllowed ? '' : ' (not in your plan)'}</span>
          </label>

          {error && <div className="error">{error}</div>}
          <button disabled={busy}>{busy ? 'Publishing… (~10–30s)' : 'Create & publish'}</button>
        </form>
      </section>

      <section className="card">
        <div className="section-head">
          <h3>Voting processes</h3>
          {!loading && items.length > 0 && <span className="count">{items.length}</span>}
        </div>
        {loading ? (
          <p className="muted">Loading…</p>
        ) : items.length === 0 ? (
          <div className="empty">No voting processes yet. Create one on the left.</div>
        ) : (
          <ul className="proposals">
            {items.map((p) => {
              const finished = isFinished(p)
              const status = finished ? 'Closed' : p.status
              return (
                <li key={p.id}>
                  <div className="p-head">
                    <span className="p-title">{p.title}</span>
                    <div className="p-actions">
                      {closing.has(p.id) ? (
                        <span className="status s-closing">Closing… ⟳</span>
                      ) : (
                        !finished && <button className="link danger" onClick={() => close(p.id)}>Close</button>
                      )}
                    </div>
                  </div>
                  {p.description && <p className="p-desc small">{p.description}</p>}
                  <div className="p-meta">
                    <span className={`status s-${status.toLowerCase()}`}>{status}</span>
                    {p.anonymous && <span className="badge-anon">anonymous</span>}
                    <span className="mono small muted" title={p.vocdoniProcessId}>{short(p.vocdoniProcessId)}</span>
                  </div>
                  <div className="p-questions">
                    {p.questions.map((q) => (
                      <div key={q.id} className="p-question-block">
                        <div className="p-question">
                          <span className="pq-kind">{q.kind}</span>
                          {q.status && <span className={`status s-${q.status.toLowerCase()}`}>{q.status}</span>}
                        </div>
                        <QuestionResults q={q} />
                        {!finished && q.upstreamId && (
                          <EligibilityEditor assoc={assoc} proposal={p} q={q} onChanged={() => api(base).then(setItems).catch(() => {})} />
                        )}
                        {q.memos?.length > 0 && (
                          <div className="memos">
                            <span className="eyebrow">Free-text answers ({q.memos.length})</span>
                            <ul>
                              {q.memos.map((m, mi) => <li key={mi}>{m}</li>)}
                            </ul>
                          </div>
                        )}
                      </div>
                    ))}
                  </div>
                  {p.vocdoniProcessId && <VotingLink processId={p.vocdoniProcessId} />}
                </li>
              )
            })}
          </ul>
        )}
      </section>

      <DangerZone assoc={assoc} />
    </div>
  )
}

// Live per-question voter eligibility (saas-backend #621). The stored restriction is the COMPLETE
// member-id list; empty = the whole census. The backend refuses (409) to strip a voter who already
// holds a ballot signature while the question runs — those members are named in the error.
function EligibilityEditor({ assoc, proposal, q, onChanged }) {
  const [open, setOpen] = useState(false)
  const [homeowners, setHomeowners] = useState(null) // lazy-loaded when the editor opens
  const [everyone, setEveryone] = useState(!(q.eligibleMemberIds?.length > 0))
  const [checked, setChecked] = useState(() => new Set(q.eligibleMemberIds ?? []))
  const [saving, setSaving] = useState(false)
  const [err, setErr] = useState('')

  const restricted = q.eligibleMemberIds?.length > 0
  const summary = restricted ? `${q.eligibleMemberIds.length} member${q.eligibleMemberIds.length === 1 ? '' : 's'}` : 'everyone'

  async function toggleOpen() {
    setErr('')
    if (!open && homeowners === null) {
      try {
        setHomeowners(await api(`/associations/${assoc.id}/homeowners`))
      } catch (e) {
        setErr(e.message)
        return
      }
    }
    // Re-seed from the current server state each time the editor opens.
    setEveryone(!(q.eligibleMemberIds?.length > 0))
    setChecked(new Set(q.eligibleMemberIds ?? []))
    setOpen(!open)
  }

  const toggleMember = (id) =>
    setChecked((s) => {
      const n = new Set(s)
      n.has(id) ? n.delete(id) : n.add(id)
      return n
    })

  async function save() {
    setErr('')
    if (!everyone && checked.size === 0) {
      setErr('Select at least one member, or choose "No restriction".')
      return
    }
    setSaving(true)
    try {
      await api(`/associations/${assoc.id}/proposals/${proposal.id}/questions/${q.id}/eligibility`, {
        method: 'PUT',
        body: { memberIds: everyone ? [] : [...checked] },
      })
      setOpen(false)
      onChanged()
    } catch (e) {
      const ids = e.data?.signedMemberIds
      const names = ids?.length
        ? ids.map((id) => {
            const h = homeowners?.find((x) => x.id === id)
            return h ? `${h.name} ${h.surname ?? ''}`.trim() : id
          })
        : null
      setErr(names ? `${e.message} Affected: ${names.join(', ')}.` : e.message)
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="eligibility">
      <span className="small muted">
        Eligibility: <b>{summary}</b>{' '}
        <button type="button" className="link" onClick={toggleOpen}>{open ? 'Cancel' : 'Edit'}</button>
      </span>
      {open && homeowners && (
        <div className="eligibility-editor">
          <label className="check small">
            <input type="radio" checked={everyone} onChange={() => setEveryone(true)} />
            <span>No restriction (everyone in the census)</span>
          </label>
          <label className="check small">
            <input type="radio" checked={!everyone} onChange={() => setEveryone(false)} />
            <span>Only these members:</span>
          </label>
          {!everyone && (
            <div className="eligibility-members">
              {homeowners.map((h) => (
                <label key={h.id} className="check small">
                  <input type="checkbox" checked={checked.has(h.id)} onChange={() => toggleMember(h.id)} />
                  <span>{h.name} {h.surname ?? ''} <span className="mono muted">#{h.memberNumber}</span></span>
                </label>
              ))}
            </div>
          )}
          {err && <div className="error small">{err}</div>}
          <button type="button" className="link" disabled={saving} onClick={save}>
            {saving ? 'Saving…' : 'Save eligibility'}
          </button>
        </div>
      )}
      {err && !open && <div className="error small">{err}</div>}
    </div>
  )
}

function VotingLink({ processId }) {
  const [copied, setCopied] = useState(false)
  const url = `${location.origin}/processes/${processId}`
  async function copy() {
    try {
      await navigator.clipboard.writeText(url)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* clipboard blocked */
    }
  }
  return (
    <div className="vote-link">
      <a href={url} target="_blank" rel="noreferrer">Voting page ↗</a>
      <button className="link" onClick={copy}>{copied ? 'Copied!' : 'Copy link'}</button>
    </div>
  )
}
