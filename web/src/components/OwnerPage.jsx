import { useEffect, useState } from 'react'
import { api } from '../api.js'
import Memberbase from './Memberbase.jsx'
import Proposals from './Proposals.jsx'

export default function OwnerPage() {
  const [assoc, setAssoc] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [tab, setTab] = useState('proposals')

  useEffect(() => {
    ;(async () => {
      try {
        const list = await api('/associations')
        setAssoc(list[0] ?? null)
      } catch (e) {
        setError(e.message)
      } finally {
        setLoading(false)
      }
    })()
  }, [])

  if (loading) return <p className="muted">Loading…</p>
  if (error) return <div className="error">{error}</div>
  if (!assoc) return <p className="muted">No association is assigned to your account.</p>

  return (
    <div>
      <div className="assoc-head">
        <h2>{assoc.name}</h2>
        <span className="org mono" title={assoc.vocdoniOrgAddress}>
          {assoc.vocdoniOrgAddress.slice(0, 10)}…{assoc.vocdoniOrgAddress.slice(-6)}
        </span>
      </div>
      <div className="tabs">
        <button className={tab === 'proposals' ? 'tab active' : 'tab'} onClick={() => setTab('proposals')}>
          Voting processes
        </button>
        <button className={tab === 'members' ? 'tab active' : 'tab'} onClick={() => setTab('members')}>
          Memberbase
        </button>
      </div>
      {tab === 'proposals' ? <Proposals assoc={assoc} /> : <Memberbase assoc={assoc} />}
    </div>
  )
}
