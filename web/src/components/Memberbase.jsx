import { useEffect, useRef, useState } from 'react'
import { api } from '../api.js'

export default function Memberbase({ assoc }) {
  const [members, setMembers] = useState([])
  const [loading, setLoading] = useState(true)
  const [form, setForm] = useState({ name: '', memberNumber: '', email: '' })
  const [error, setError] = useState('')
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const fileRef = useRef(null)

  const base = `/associations/${assoc.id}/homeowners`

  async function load() {
    setLoading(true)
    try {
      setMembers(await api(base))
    } catch (e) {
      setError(e.message)
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => {
    load()
  }, [assoc.id])

  async function add(e) {
    e.preventDefault()
    setError('')
    setNote('')
    setBusy(true)
    try {
      const body = { name: form.name, memberNumber: form.memberNumber }
      if (form.email) body.email = form.email
      await api(base, { method: 'POST', body })
      setForm({ name: '', memberNumber: '', email: '' })
      await load()
    } catch (e) {
      setError(e.message)
    } finally {
      setBusy(false)
    }
  }

  async function remove(id) {
    if (!confirm('Remove this homeowner?')) return
    setError('')
    try {
      await api(`${base}/${id}`, { method: 'DELETE' })
      await load()
    } catch (e) {
      setError(e.message)
    }
  }

  async function importCsv(e) {
    const file = e.target.files?.[0]
    if (!file) return
    setError('')
    setNote('')
    setBusy(true)
    try {
      const rows = parseCsv(await file.text())
      let added = 0
      for (const r of rows) {
        const body = { name: r.name, memberNumber: r.memberNumber }
        if (r.email) body.email = r.email
        try {
          await api(base, { method: 'POST', body })
          added++
        } catch {
          /* skip duplicates / errors per row */
        }
      }
      await load()
      setNote(`Imported ${added} of ${rows.length} rows.`)
    } catch (e) {
      setError(e.message)
    } finally {
      setBusy(false)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  return (
    <div className="grid">
      <section className="card">
        <h3>Add homeowner</h3>
        <form onSubmit={add}>
          <label>
            Name
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </label>
          <label>
            Member number
            <input value={form.memberNumber} onChange={(e) => setForm({ ...form, memberNumber: e.target.value })} required />
          </label>
          <label>
            Email (optional)
            <input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
          </label>
          <button disabled={busy}>Add</button>
        </form>

        <hr />

        <h3>Import CSV</h3>
        <p className="helper">
          Columns: <code>First Name, Member Number</code> (optional <code>Email</code>). Member numbers must be unique.
        </p>
        <input ref={fileRef} type="file" accept=".csv,text/csv" onChange={importCsv} disabled={busy} />
        {busy && <p className="muted small">Working…</p>}
        {note && <div className="note">{note}</div>}
        {error && <div className="error">{error}</div>}
      </section>

      <section className="card">
        <div className="section-head">
          <h3>Homeowners</h3>
          {!loading && members.length > 0 && <span className="count">{members.length}</span>}
        </div>
        {loading ? (
          <p className="muted">Loading…</p>
        ) : members.length === 0 ? (
          <div className="empty">No homeowners yet. Add them above or import a CSV.</div>
        ) : (
          <table>
            <thead>
              <tr><th>Name</th><th>Member #</th><th></th></tr>
            </thead>
            <tbody>
              {members.map((m) => (
                <tr key={m.id}>
                  <td>{m.name}</td>
                  <td>{m.memberNumber}</td>
                  <td>
                    <button className="link danger" onClick={() => remove(m.id)}>Remove</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  )
}

// Parses "First Name,Member Number" or "First Name,Email,Member Number"; skips the header row.
function parseCsv(text) {
  const lines = text.split(/\r?\n/).filter((l) => l.trim())
  return lines
    .slice(1)
    .map((line) => {
      const c = line.split(',').map((x) => x.trim())
      return c.length >= 3
        ? { name: c[0], email: c[1], memberNumber: c[2] }
        : { name: c[0], memberNumber: c[1] }
    })
    .filter((r) => r.name && r.memberNumber)
}
