import type { RunStatus } from '../api/types'

const labels: Record<RunStatus, string> = {
  created: 'создан',
  running: 'идёт',
  completed: 'завершён',
  failed: 'сбой',
  aborted: 'остановлен',
}

export function StatusBadge({ status }: { status: RunStatus }) {
  return <span className={`status status-${status}`}>{labels[status]}</span>
}
