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
  // Тики, которые агент не отправил: все воркеры были заняты. Замер такой ступени неточный.
  skipped_count?: number
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
  started_at?: string | null
  finished_at?: string | null
  // Причина failed (или истёкшего срока у aborted), текст для человека
  error?: string | null
  // Полные ступени по мере прихода от агента
  steps?: StepResult[]
  // Предел появится, когда координатор начнёт вызывать анализатор (#34)
  limit?: Limit | null
}
