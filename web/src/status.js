// Has a proposal/process finished? Shared by the public voting page and the owner admin panel so the
// two never disagree. Works on either shape: on-chain status when present (voting page), else the
// stored status + end date (admin list, which has no on-chain status).
export function isFinished(info) {
  const oc = (info.onchainStatus || '').toUpperCase()
  if (oc === 'RESULTS' || oc === 'ENDED' || oc === 'CANCELED') return true
  if (info.status === 'Closed') return true
  if (info.endDate && new Date(info.endDate).getTime() < Date.now()) return true
  return false
}
