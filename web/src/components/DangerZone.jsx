import { useState } from 'react'
import { api } from '../api.js'
import { clearAuth } from '../auth.js'

// Self-service association delete. Lives in the Owner's sidebar (under the new-process form).
export default function DangerZone({ assoc }) {
  const [error, setError] = useState('')

  async function remove() {
    if (!confirm(`Permanently delete "${assoc.name}"?\n\nThis deletes the association, its proposals, and the Vocdoni organization (members, censuses, processes). You will be logged out. This cannot be undone.`))
      return
    setError('')
    try {
      await api(`/associations/${assoc.id}`, { method: 'DELETE' })
      clearAuth()
      location.reload() // account is gone → back to Login (mirrors api.js 401 path)
    } catch (e) {
      setError(e.message) // 409 surfaces "close active proposals first"
    }
  }

  return (
    <section className="card danger-zone">
      <h3>Danger zone</h3>
      <p className="helper">
        Permanently delete this association and its Vocdoni organization. Close any active voting
        processes first. This cannot be undone and logs you out.
      </p>
      {error && <div className="error">{error}</div>}
      <button className="danger" onClick={remove}>Delete this association</button>
    </section>
  )
}
