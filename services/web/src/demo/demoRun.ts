import type { Run, StepResult } from '../api/types'
// Копия examples/result.json: файлы вне services/web не попадают в сборку Docker
import result from './result.json'

export const DEMO_RUN_ID = 'demo'

// Демо-прогон, чтобы проверить экраны, пока координатор не отдаёт ступени и предел.
// Предел - то, что выдаёт анализатор на этом файле с порогами по умолчанию.
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
  steps: result.steps as StepResult[],
  limit: {
    found: true,
    limit_rps: 350,
    failed_step_index: 7,
    reasons: [
      'p99 812 мс выше порога 500 мс',
      'пропускная выросла на 3 запр/с при росте нагрузки на 50 запр/с',
      'ошибок 2.4 % при пороге 1 %',
    ],
  },
}

// Остальные состояния отчёта и живой прогон, чтобы их можно было посмотреть без координатора
const steps = demoRun.steps!

export const demoRuns: Record<string, () => Run> = {
  [DEMO_RUN_ID]: () => demoRun,
  'demo-aborted': () => ({
    ...demoRun,
    id: 'demo-aborted',
    status: 'aborted',
    finished_at: '2026-10-01T12:02:16Z',
    steps: steps.slice(0, 3),
    limit: { found: false, limit_rps: 200, failed_step_index: null, reasons: [] },
  }),
  'demo-failed': () => ({
    ...demoRun,
    id: 'demo-failed',
    status: 'failed',
    finished_at: '2026-10-01T12:01:31Z',
    error: 'Агент недоступен: Status(StatusCode="Unavailable", Detail="failed to connect to all addresses")',
    steps: steps.slice(0, 2),
    limit: null,
  }),
  'demo-skipped': () => ({
    ...demoRun,
    id: 'demo-skipped',
    steps: steps.map((s) => (s.index >= 6 ? { ...s, skipped_count: s.index === 6 ? 120 : 950 } : s)),
    // Анализатор берёт только ступени до первой с пропусками
    limit: { found: false, limit_rps: 300, failed_step_index: null, reasons: [] },
  }),
  // Идёт четвёртая ступень: время старта считается от текущего момента
  'demo-live': () => ({
    ...demoRun,
    id: 'demo-live',
    status: 'running',
    started_at: new Date(Date.now() - 160_000).toISOString(),
    finished_at: null,
    steps: steps.slice(0, 3),
    limit: null,
  }),
}

export function isDemoRun(id: string): boolean {
  return Object.hasOwn(demoRuns, id)
}
