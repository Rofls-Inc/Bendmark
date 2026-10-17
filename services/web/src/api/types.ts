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
  // Тики, которые агент не отправил: не было свободного рабочего потока
  skipped_count: number
}

// pending — прогон завершён, анализатор ещё не ответил
export type AnalysisStatus = 'pending' | 'found' | 'not_found' | 'undetermined' | 'error'

// Поиск предела, как в Analysis.cs координатора
export interface Analysis {
  status: AnalysisStatus
  // found: последняя ступень без отказа (null, если отказ на первой); not_found: нижняя оценка
  limit_rps: number | null
  failed_step_index: number | null
  reasons: string[]
  // Сколько первых ступеней ушло анализатору
  analyzed_steps: number
  // Итог для человека от координатора
  message: string
}

export interface Run {
  id: string
  scenario: Scenario
  status: RunStatus
  created_at: string
  started_at: string | null
  finished_at: string | null
  // Причина для failed и части aborted
  error: string | null
  steps: StepResult[]
  // null, пока прогон не завершён, и у failed или без ступеней
  analysis: Analysis | null
}

// Строка списка GET /runs: без сценария и ступеней
export interface RunSummary {
  id: string
  scenario_name: string
  status: RunStatus
  created_at: string
  started_at: string | null
  finished_at: string | null
  // Из analysis прогона, null, пока анализа нет
  analysis_status: AnalysisStatus | null
  limit_rps: number | null
}

export interface RunList {
  runs: RunSummary[]
  // id для следующей страницы (?before=), null - страниц больше нет
  next_before: string | null
}
