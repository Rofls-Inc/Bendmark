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
