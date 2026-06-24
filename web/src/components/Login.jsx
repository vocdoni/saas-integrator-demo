import { useState } from 'react'
import { api } from '../api.js'
import { setAuth } from '../auth.js'
import Mark from './Mark.jsx'

export default function Login({ onLogin }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(e) {
    e.preventDefault()
    setError('')
    setBusy(true)
    try {
      const res = await api('/auth/login', { method: 'POST', body: { email, password } })
      const auth = { token: res.token, role: res.role, email }
      setAuth(auth)
      onLogin(auth)
    } catch {
      setError('Invalid email or password')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login">
      <div className="login-inner">
        <div className="login-brand">
          <Mark />
          <div>
            <h1>Homeowners Voting Platform</h1>
            <p className="muted" style={{ margin: '2px 0 0' }}>Sign in to manage community ballots</p>
          </div>
        </div>
        <form className="card" onSubmit={submit}>
          <label>
            Email
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoFocus />
          </label>
          <label>
            Password
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required />
          </label>
          {error && <div className="error">{error}</div>}
          <button disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
        </form>
      </div>
    </div>
  )
}
