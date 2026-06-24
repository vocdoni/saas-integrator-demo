import { getToken, clearAuth } from './auth.js'

// fetch wrapper: prefixes /api, attaches the Bearer token, throws a useful Error on non-2xx.
export async function api(path, { method = 'GET', body } = {}) {
  const headers = { 'Content-Type': 'application/json' }
  const token = getToken()
  if (token) headers.Authorization = `Bearer ${token}`

  const res = await fetch(`/api${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (res.status === 401) {
    clearAuth()
    location.reload()
    throw new Error('Session expired')
  }

  const text = await res.text()
  const data = text ? safeJson(text) : null
  if (!res.ok) {
    const msg = data?.detail || data?.error || data?.title || (typeof data === 'string' ? data : '') || `HTTP ${res.status}`
    throw new Error(msg)
  }
  return data
}

function safeJson(t) {
  try {
    return JSON.parse(t)
  } catch {
    return t
  }
}
