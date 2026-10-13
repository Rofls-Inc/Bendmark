import type { Run, StepResult } from '../api/types'
// Копия testdata/results/limit-found.json: файлы вне services/web не попадают в сборку Docker.
// CI сверяет копию с оригиналом (web.yml).
import result from './result.json'

export const DEMO_RUN_ID = 'demo'

// Демо-прогон, чтобы посмотреть отчёт без координатора и агента.
// Анализ — то, что координатор отдаёт для этого файла с порогами анализатора по умолчанию.
export const demoRun: Run = {
  id: DEMO_RUN_ID,
  scenario: {
    name: result.scenario_name,
    target: { url: 'http://abstock:8080/', method: 'GET' },
    steps: result.steps.map((s) => ({ target_rps: s.target_rps, duration_seconds: s.duration_seconds })),
  },
  status: 'completed',
  created_at: '2026-10-01T12:00:00Z',
  started_at: '2026-10-01T12:00:01Z',
  finished_at: '2026-10-01T12:05:16Z',
  error: null,
  // В файле нет skipped_count, а координатор отдаёт его всегда
  steps: result.steps.map((s): StepResult => ({ ...s, skipped_count: 0 })),
  analysis: {
    status: 'found',
    limit_rps: 350,
    failed_step_index: 7,
    reasons: [
      'p99 812 мс выше порога 500 мс',
      'пропускная выросла на 3 запр/с при росте нагрузки на 50 запр/с',
      'ошибок 2.4 % при пороге 1 %',
    ],
    analyzed_steps: 7,
    message: 'Предел: 350 запр/с, отказ на ступени 7 (400 запр/с)',
  },
}

// Остальные состояния отчёта и живой прогон, чтобы их можно было посмотреть без координатора
const steps = demoRun.steps

export const demoRuns: Record<string, () => Run> = {
  [DEMO_RUN_ID]: () => demoRun,
  'demo-aborted': () => ({
    ...demoRun,
    id: 'demo-aborted',
    status: 'aborted',
    finished_at: '2026-10-01T12:02:16Z',
    steps: steps.slice(0, 3),
    analysis: {
      status: 'not_found',
      limit_rps: 200,
      failed_step_index: null,
      reasons: [],
      analyzed_steps: 3,
      message: 'Прогон остановлен, анализ только по завершённым ступеням. Отказа не было: предел не ниже 200 запр/с',
    },
  }),
  'demo-failed': () => ({
    ...demoRun,
    id: 'demo-failed',
    status: 'failed',
    finished_at: '2026-10-01T12:01:31Z',
    error: 'Агент недоступен: Status(StatusCode="Unavailable", Detail="failed to connect to all addresses")',
    steps: steps.slice(0, 2),
    analysis: null,
  }),
  'demo-skipped': () => ({
    ...demoRun,
    id: 'demo-skipped',
    steps: steps.map((s) => (s.index >= 6 ? { ...s, skipped_count: s.index === 6 ? 120 : 950 } : s)),
    // Анализатор берёт только ступени до первой с пропусками
    analysis: {
      status: 'not_found',
      limit_rps: 300,
      failed_step_index: null,
      reasons: [],
      analyzed_steps: 5,
      message: 'Отказа не было до ступени 6: предел не ниже 300 запр/с, дальше агент не успевал подавать нагрузку',
    },
  }),
  // Идёт четвёртая ступень: время старта считается от текущего момента
  'demo-live': () => ({
    ...demoRun,
    id: 'demo-live',
    status: 'running',
    started_at: new Date(Date.now() - 160_000).toISOString(),
    finished_at: null,
    steps: steps.slice(0, 3),
    analysis: null,
  }),
}

export function isDemoRun(id: string): boolean {
  return Object.hasOwn(demoRuns, id)
}
