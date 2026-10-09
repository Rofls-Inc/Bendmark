// Сущности из docs/domain.md. Имена полей как в JSON координатора (snake_case).

export interface Target {
  url: string
  method: string
}

export interface Step {
  target_rps: number
  duration_seconds: number
}

export interface Scenario {
  name: string
  target: Target
  steps: Step[]
}

export type RunStatus = 'created' | 'running' | 'completed' | 'failed' | 'aborted'

export const FINAL_STATUSES: readonly RunStatus[] = ['completed', 'failed', 'aborted']

export interface StepResult {
  index: number
  target_rps: number
  duration_seconds: number
  request_count: number
  throughput_rps: number
  latency_ms: { p50: number; p90: number; p99: number }
  errors: { count: number; rate_percent: number }
}

export interface Limit {
  found: boolean
  limit_rps?: number | null
  failed_step_index?: number | null
  reasons: string[]
}

export interface Run {
  id: string
  scenario: Scenario
  status: RunStatus
  created_at: string
  // Результаты ступеней придут из агента (#33), предел из анализатора (#34).
  // Пока координатор их не отдаёт, поэтому поля необязательные.
  steps?: StepResult[]
  limit?: Limit | null
}
