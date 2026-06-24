// Brand mark: a marked ballot — a brass check stamped in an ink square.
export default function Mark() {
  return (
    <span className="mark" aria-hidden="true">
      <svg viewBox="0 0 24 24" fill="none">
        <path
          d="M5 12.5l4.3 4.3L19 7"
          stroke="#cda657"
          strokeWidth="2.6"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </svg>
    </span>
  )
}
