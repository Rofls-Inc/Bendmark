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
  steps: result.steps as StepResult[],
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
