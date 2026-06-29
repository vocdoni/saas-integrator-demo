import { useEffect, useState } from 'react'
import { api } from '../api.js'
import DangerZone from './DangerZone.jsx'

const pad = (n) => String(n).padStart(2, '0')
const toLocalInput = (d) =>
  `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
const short = (s) => (s ? `${s.slice(0, 10)}…${s.slice(-6)}` : '')
const ONE_DAY = 864e5
const plusDay = (localStr) => toLocalInput(new Date(new Date(localStr).getTime() + ONE_DAY))

const blankForm = () => {
  const start = toLocalInput(new Date())
  return {
    title: '',
    description: '',
    choices: ['Yes', 'No'],
    startDate: start,
    endDate: plusDay(start), // default end = start + 1 day
    allowMultiple: false,
    twoFactorAuth: false,
  }
}

export default function Proposals({ assoc }) {
  const [items, setItems] = useState([])
  const [loading, setLoading] = useState(true)
  const [form, setForm] = useState(blankForm())
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [results, setResults] = useState({})

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
  }, [assoc.id])

  const setChoice = (i, v) => setForm((f) => ({ ...f, choices: f.choices.map((c, j) => (j === i ? v : c)) }))
  const addChoice = () => setForm((f) => ({ ...f, choices: [...f.choices, ''] }))
  const removeChoice = (i) => setForm((f) => ({ ...f, choices: f.choices.filter((_, j) => j !== i) }))

  async function create(e) {
    e.preventDefault()
    setError('')
    setBusy(true)
    try {
      const choices = form.choices.map((c) => c.trim()).filter(Boolean)
      if (choices.length < 2) throw new Error('Add at least two choices.')
      await api(base, {
        method: 'POST',
        body: {
          title: form.title,
          description: form.description,
          choices: choices.map((title) => ({ title })),
          startDate: new Date(form.startDate).toISOString(),
          endDate: new Date(form.endDate).toISOString(),
          allowMultiple: form.allowMultiple,
          twoFactorAuth: form.twoFactorAuth,
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

  async function close(pid) {
    if (!confirm('Close voting on this process?')) return
    setError('')
    try {
      await api(`${base}/${pid}/close`, { method: 'POST' })
      await load()
    } catch (e) {
      setError(e.message)
    }
  }

  async function toggleResults(pid) {
    // Already shown → hide.
    if (results[pid]) {
      setResults((prev) => {
        const next = { ...prev }
        delete next[pid]
        return next
      })
      return
    }
    setError('')
    try {
      const r = await api(`${base}/${pid}/results`)
      setResults((prev) => ({ ...prev, [pid]: r }))
    } catch (e) {
      setError(e.message)
    }
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

          <div className="choices">
            <span className="label">Choices</span>
            {form.choices.map((c, i) => (
              <div key={i} className="choice-row">
                <input value={c} onChange={(e) => setChoice(i, e.target.value)} placeholder={`Choice ${i + 1}`} />
                {form.choices.length > 2 && (
                  <button type="button" className="link danger" onClick={() => removeChoice(i)}>×</button>
                )}
              </div>
            ))}
            <button type="button" className="link" onClick={addChoice}>+ Add choice</button>
          </div>

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

          <label className="check">
            <input type="checkbox" checked={form.allowMultiple} onChange={(e) => setForm({ ...form, allowMultiple: e.target.checked })} />
            Allow multiple selections
          </label>
          <label className="check">
            <input type="checkbox" checked={form.twoFactorAuth} onChange={(e) => setForm({ ...form, twoFactorAuth: e.target.checked })} />
            Require email 2FA (off = authenticate by member number)
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
            {items.map((p) => (
              <li key={p.id}>
                <div className="p-head">
                  <span className="p-title">{p.title}</span>
                  <div className="p-actions">
                    <button className="link" onClick={() => toggleResults(p.id)}>
                      {results[p.id] ? 'Hide results' : 'Results'}
                    </button>
                    {p.status !== 'Closed' && (
                      <button className="link danger" onClick={() => close(p.id)}>Close</button>
                    )}
                  </div>
                </div>
                {p.description && <p className="p-desc small">{p.description}</p>}
                <div className="p-meta">
                  <span className={`status s-${(p.status || '').toLowerCase()}`}>{p.status}</span>
                  <span className="mono small muted" title={p.vocdoniProcessId}>{short(p.vocdoniProcessId)}</span>
                </div>
                {p.vocdoniProcessId && <VotingLink processId={p.vocdoniProcessId} />}
                {results[p.id] && (
                  <div className="results">
                    <div className="results-meta">
                      On-chain status <strong>{results[p.id].status}</strong> ·{' '}
                      <span className="num">{results[p.id].voteCount}</span> vote{results[p.id].voteCount === 1 ? '' : 's'}
                      {results[p.id].censusSize > 0 && (
                        <> of <span className="num">{results[p.id].censusSize}</span> eligible</>
                      )}
                    </div>
                    <Tally result={results[p.id]} choices={p.choices} />
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>

      <DangerZone assoc={assoc} />
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

// Tally bars for the first (only) question, filled against the census size (eligible voters,
// from the demo API), so each bar shows turnout share — not share of the leading choice. Falls
// back to the leading tally if the census size is unavailable.
function Tally({ result, choices }) {
  const row = result.results?.[0]
  if (!row) return null
  const nums = row.map((v) => Number(v) || 0)
  const denom = result.censusSize > 0 ? result.censusSize : Math.max(1, ...nums)
  return (
    <div className="tally">
      {row.map((v, ci) => (
        <div className="bar" key={ci}>
          <span className="bar-label">{choices?.[ci] ?? `Choice ${ci}`}</span>
          <span className="bar-track">
            <span className="bar-fill" style={{ width: `${Math.min(100, (nums[ci] / denom) * 100)}%` }} />
          </span>
          <span className="bar-val">{v}</span>
        </div>
      ))}
    </div>
  )
}
