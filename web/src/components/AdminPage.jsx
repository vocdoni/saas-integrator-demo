import { useEffect, useState } from 'react'
import { api } from '../api.js'

const short = (s) => (s ? `${s.slice(0, 8)}…${s.slice(-4)}` : '')

// Integrator wallet + managed-org cap (saas-backend #819). info: null = loading, false = unavailable.
function IntegratorStatus({ info }) {
  if (info === null) return null
  if (info === false) return <p className="helper">Integrator status unavailable.</p>
  const n = info.managedOrgsCreated
  return (
    <>
      <p className="helper">
        Integrator wallet: <b>{info.balanceCents == null ? 'unavailable' : `€${(info.balanceCents / 100).toFixed(2)}`}</b>
        {' · '}
        {n} association{n === 1 ? '' : 's'} created
      </p>
      {!info.canCreateManagedOrg && (
        <div className="error">Top up the integrator wallet in the Vocdoni portal before creating another association.</div>
      )}
    </>
  )
}

export default function AdminPage() {
  const [assocs, setAssocs] = useState([])
  const [loading, setLoading] = useState(true)
  const [form, setForm] = useState({ name: '', ownerEmail: '', ownerPassword: '' })
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [integrator, setIntegrator] = useState(null)

  async function load() {
    setLoading(true)
    try {
      setAssocs(await api('/associations'))
    } catch (e) {
      setError(e.message)
    } finally {
      setLoading(false)
    }
  }
  // Separate from load(): a failing integrator read must not block the associations list.
  const loadIntegrator = () => api('/integrator').then(setIntegrator).catch(() => setIntegrator(false))
  useEffect(() => {
    load()
    loadIntegrator()
  }, [])

  async function create(e) {
    e.preventDefault()
    setError('')
    setBusy(true)
    try {
      await api('/associations', { method: 'POST', body: form })
      setForm({ name: '', ownerEmail: '', ownerPassword: '' })
      await load()
      loadIntegrator()
    } catch (e) {
      setError(e.message)
    } finally {
      setBusy(false)
    }
  }

  async function remove(a) {
    if (!confirm(`Permanently delete "${a.name}"?\n\nThis deletes the association, its proposals and owner login, and the Vocdoni organization (members, censuses, processes). Close any active voting processes first. This cannot be undone.`))
      return
    setError('')
    try {
      await api(`/associations/${a.id}`, { method: 'DELETE' })
      await load()
    } catch (e) {
      setError(e.message)
    }
  }

  return (
    <div className="grid">
      <section className="card">
        <h2>Create association</h2>
        <form onSubmit={create}>
          <label>
            Association name
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </label>
          <label>
            Owner email
            <input type="email" value={form.ownerEmail} onChange={(e) => setForm({ ...form, ownerEmail: e.target.value })} required />
          </label>
          <label>
            Owner password
            <input type="password" value={form.ownerPassword} onChange={(e) => setForm({ ...form, ownerPassword: e.target.value })} required />
          </label>
          {error && <div className="error">{error}</div>}
          <button disabled={busy}>{busy ? 'Creating…' : 'Create association'}</button>
        </form>
        <p className="helper">Creates a Vocdoni managed organization and an owner login that can manage it.</p>
        <IntegratorStatus info={integrator} />
      </section>

      <section className="card">
        <div className="section-head">
          <h2>Associations</h2>
          {!loading && assocs.length > 0 && <span className="count">{assocs.length}</span>}
        </div>
        {loading ? (
          <p className="muted">Loading…</p>
        ) : assocs.length === 0 ? (
          <div className="empty">No associations yet. Create the first one on the left.</div>
        ) : (
          <table>
            <thead>
              <tr><th>#</th><th>Name</th><th>Owner</th><th>Vocdoni org</th><th></th></tr>
            </thead>
            <tbody>
              {assocs.map((a) => (
                <tr key={a.id}>
                  <td>{a.id}</td>
                  <td>{a.name}</td>
                  <td>{a.ownerEmail}</td>
                  <td className="mono" title={a.vocdoniOrgAddress}>{short(a.vocdoniOrgAddress)}</td>
                  <td><button className="link danger" onClick={() => remove(a)}>Remove</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  )
}
