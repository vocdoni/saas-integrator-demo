import { useState } from 'react'
import { getAuth, clearAuth } from './auth.js'
import Login from './components/Login.jsx'
import AdminPage from './components/AdminPage.jsx'
import OwnerPage from './components/OwnerPage.jsx'
import VotingPage from './components/VotingPage.jsx'
import Mark from './components/Mark.jsx'

export default function App() {
  // Public voting page: /processes/<onchainProcessId> — rendered without the login gate.
  const voting = window.location.pathname.match(/^\/processes\/([^/]+)/)
  if (voting) return <VotingPage processId={voting[1]} />
  return <AuthedApp />
}

function AuthedApp() {
  const [auth, setAuth] = useState(getAuth())

  if (!auth) return <Login onLogin={setAuth} />

  const logout = () => {
    clearAuth()
    setAuth(null)
  }

  const isAdmin = auth.role === 'SuperAdmin'
  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <Mark />
          <div>
            <div className="brand-name">Homeowners Voting Platform</div>
            <div className="brand-sub">Community ballots</div>
          </div>
        </div>
        <div className="who">
          <span className="badge">{isAdmin ? 'Backend admin' : 'Association admin'}</span>
          <span className="muted">{auth.email}</span>
          <button className="link" onClick={logout}>Log out</button>
        </div>
      </header>
      <main>{isAdmin ? <AdminPage /> : <OwnerPage />}</main>
    </div>
  )
}
