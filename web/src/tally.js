// Per-option numbers from a question's on-chain results histogram, by kind:
// - single: results[0] is the per-choice count.
// - multiple (approval): one field per choice, each [#voted-0, #voted-1] → count is field[1].
// - ranked: one field per choice, a histogram over rank values → Borda score Σ count·value.
// - cumulative: one field per choice, a histogram over credit amounts → total credits Σ count·value
//   (same index-weighted fold as ranked).
export function tallyCounts(results, kind) {
  if (!results || !results[0]) return null
  if (kind === 'multiple') return results.map((f) => Number(f?.[1]) || 0)
  if (kind === 'ranked' || kind === 'cumulative')
    return results.map((f) => f.reduce((s, c, v) => s + (Number(c) || 0) * v, 0))
  return results[0].map((v) => Number(v) || 0)
}
