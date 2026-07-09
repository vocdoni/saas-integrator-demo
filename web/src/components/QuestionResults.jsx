import { tallyCounts } from '../tally.js'

// One question's on-chain tally. `q` = { title, choices, kind, voteCount, results }. Bars fill against
// the leading value (single/multiple = the top choice's count; ranked = the top Borda score).
export default function QuestionResults({ q }) {
  const nums = tallyCounts(q.results, q.kind)
  const max = nums ? Math.max(...nums, 0) : 0
  return (
    <div className="q-results">
      <div className="results-meta">
        <strong>{q.title}</strong> · <span className="num">{q.voteCount ?? 0}</span> vote{q.voteCount === 1 ? '' : 's'}
      </div>
      {!nums || max === 0 ? (
        <div className="vote-soon">No votes yet.</div>
      ) : (
        <div className="tally">
          {nums.map((n, ci) => (
            <div className={`bar${n === max ? ' win' : ''}`} key={ci}>
              <span className="bar-label">{q.choices?.[ci] ?? `Choice ${ci}`}</span>
              <span className="bar-track">
                <span className="bar-fill" style={{ width: `${Math.min(100, (n / max) * 100)}%` }} />
              </span>
              <span className="bar-val">{n}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
