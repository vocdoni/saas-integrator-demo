// Minimal auth store over localStorage. Holds { token, role, email }.
const KEY = 'hoa.auth'

export function getAuth() {
  try {
    return JSON.parse(localStorage.getItem(KEY))
  } catch {
    return null
  }
}

export function setAuth(auth) {
  localStorage.setItem(KEY, JSON.stringify(auth))
}

export function clearAuth() {
  localStorage.removeItem(KEY)
}

export function getToken() {
  return getAuth()?.token ?? null
}
