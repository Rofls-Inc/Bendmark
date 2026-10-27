import { describe, expect, it } from 'vitest'
import type { Analysis, Run, StepResult } from '../api/types'
import { demoRun, demoRuns } from '../demo/demoRun'
import {
  currentStep,
  firstSkippedStep,
  formatClock,
  formatDuration,
  headline,
  latencyAxis,
  needsPolling,
  p99Axis,
  progress,
  rpsAxis,
  stepVerdict,
  toChartPoints,
  toLatencyPoints,
} from './view'

const step = (index: number, overrides: Partial<StepResult> = {}): StepResult => ({
  index,
  target_rps: index * 100,
  duration_seconds: 10,
  request_count: index * 1000,
  throughput_rps: index * 100,
  latency_ms: { p50: 10, p90: 20, p99: 30 },
  errors: { count: 0, rate_percent: 0 },
  skipped_count: 0,
  ...overrides,
})

const analysis = (overrides: Partial<Analysis>): Analysis => ({
  status: 'found',
  limit_rps: null,
  failed_step_index: null,
  reasons: [],
  analyzed_steps: 4,
  message: 'итог координатора',
  ...overrides,
})

describe('toChartPoints', () => {
  it('сортирует по номеру ступени', () => {
    expect(toChartPoints([step(2), step(1)]).map((p) => p.target)).toEqual([100, 200])
  })

  it('ступень без измерений не рисуется на логарифмической оси p99', () => {
    const empty = step(1, { request_count: 0, latency_ms: { p50: 0, p90: 0, p99: 0 } })
    expect(toChartPoints([empty])[0].p99).toBeNull()
  })

  it('демо-прогон даёт точку на каждую ступень', () => {
    const points = toChartPoints(demoRun.steps)
    expect(points).toHaveLength(7)
    expect(points.at(-1)).toEqual({ target: 400, throughput: 341, p99: 812 })
  })
})

describe('stepVerdict', () => {
  it('норма, предел, отказ и ступени после отказа', () => {
    const found = analysis({ limit_rps: 200, failed_step_index: 3, reasons: ['p99'] })
    expect([1, 2, 3, 4].map((i) => stepVerdict(step(i), found))).toEqual(['ok', 'limit', 'failed', 'after'])
  })

  it('без отказа все ступени в норме', () => {
    const lowerBound = analysis({ status: 'not_found', limit_rps: 300, analyzed_steps: 3 })
    expect([1, 2, 3].map((i) => stepVerdict(step(i), lowerBound))).toEqual(['ok', 'ok', 'ok'])
  })

  it('ступени, не ушедшие анализатору, подписаны «не анализ.», а не «норма»', () => {
    const cut = analysis({ status: 'not_found', limit_rps: 200, analyzed_steps: 2 })
    expect([1, 2, 3, 4].map((i) => stepVerdict(step(i), cut))).toEqual(['ok', 'ok', 'not-analyzed', 'not-analyzed'])
  })

  it('отказ на первой ступени: предела нет', () => {
    const first = analysis({ failed_step_index: 1, reasons: ['ошибки'] })
    expect([1, 2].map((i) => stepVerdict(step(i), first))).toEqual(['failed', 'after'])
  })

  it.each(['pending', 'undetermined', 'error'] as const)('при статусе %s подписей нет', (status) => {
    expect(stepVerdict(step(1), analysis({ status }))).toBeNull()
  })

  it('без анализа подписи нет', () => {
    expect(stepVerdict(step(1), null)).toBeNull()
  })
})

describe('headline', () => {
  const run = (value: Analysis | null, overrides: Partial<Run> = {}): Run => ({ ...demoRun, analysis: value, ...overrides })

  it('предел из демо-прогона 350 запр/с с подписью координатора', () => {
    expect(headline(demoRun)).toEqual({
      kind: 'limit',
      value: 350,
      caption: 'Предел: 350 запр/с, отказ на ступени 7 (400 запр/с)',
    })
  })

  it('нижняя оценка, если отказа не было', () => {
    expect(headline(run(analysis({ status: 'not_found', limit_rps: 400 }))))
      .toMatchObject({ kind: 'lower-bound', value: 400 })
  })

  it('предел ниже первой ступени: значение — её нагрузка, карточка покажет «< 100»', () => {
    expect(headline(run(analysis({ failed_step_index: 1, reasons: ['x'] }))))
      .toMatchObject({ kind: 'below-first', value: 100 })
  })

  it('анализ идёт', () => {
    expect(headline(run(analysis({ status: 'pending', message: 'Анализ выполняется' }))))
      .toEqual({ kind: 'pending', value: null, caption: 'Анализ выполняется' })
  })

  it.each(['undetermined', 'error'] as const)('при статусе %s показывает текст координатора', (status) => {
    const message = 'Предел не определён: агент не успевал подавать нагрузку'
    expect(headline(run(analysis({ status, message })))).toEqual({ kind: 'none', value: null, caption: message })
  })

  it('у failed-прогона анализа нет', () => {
    expect(headline(run(null, { status: 'failed' }))).toMatchObject({ kind: 'none', value: null })
  })
})

describe('needsPolling', () => {
  const run = (status: Run['status'], value: Analysis | null): Run => ({ ...demoRun, status, analysis: value })

  it('опрашивает, пока прогон идёт', () => {
    expect(needsPolling(run('created', null))).toBe(true)
    expect(needsPolling(run('running', null))).toBe(true)
  })

  it('после завершения опрашивает, пока анализ в pending', () => {
    expect(needsPolling(run('completed', analysis({ status: 'pending' })))).toBe(true)
    expect(needsPolling(run('aborted', analysis({ status: 'pending' })))).toBe(true)
  })

  it('останавливается, когда анализатор ответил или анализа не будет', () => {
    expect(needsPolling(run('completed', analysis({ status: 'found' })))).toBe(false)
    expect(needsPolling(run('completed', analysis({ status: 'undetermined' })))).toBe(false)
    expect(needsPolling(run('completed', analysis({ status: 'error' })))).toBe(false)
    expect(needsPolling(run('failed', null))).toBe(false)
  })
})

describe('currentStep', () => {
  it('следующая после пришедших, но не больше числа ступеней', () => {
    const running = (steps: StepResult[]): Run => ({ ...demoRun, status: 'running', steps })
    expect(currentStep(running([]))).toBe(1)
    expect(currentStep(running([step(1), step(2)]))).toBe(3)
    expect(currentStep(running(demoRun.steps))).toBe(7)
  })

  it('не идёт, если прогон не в running', () => {
    expect(currentStep(demoRun)).toBeNull()
  })
})

describe('formatDuration', () => {
  it.each([[45, '45 с'], [60, '1 мин'], [315, '5 мин 15 с']])('%d с -> %s', (seconds, text) => {
    expect(formatDuration(seconds)).toBe(text)
  })
})

describe('оси графика', () => {
  const points = toChartPoints(demoRun.steps)

  it('ось нагрузки от нуля до круглого значения', () => {
    expect(rpsAxis(points)).toEqual({ domain: [0, 500], ticks: [0, 100, 200, 300, 400, 500] })
  })

  it('ось p99 линейная от нуля, деления через равные миллисекунды', () => {
    expect(p99Axis(points)).toEqual({ domain: [0, 1000], ticks: [0, 250, 500, 750, 1000] })
  })

  it('порог попадает на ось, даже если все p99 намного ниже', () => {
    const fast = toChartPoints([step(1, { latency_ms: { p50: 1, p90: 2, p99: 3 } })])
    expect(p99Axis(fast).domain).toEqual([0, 600])
  })

  it('без измерений p99 ось строится по порогу', () => {
    expect(p99Axis([]).domain).toEqual([0, 600])
  })
})

describe('toLatencyPoints', () => {
  it('все три перцентиля, ступень без измерений пустая', () => {
    const empty = step(2, { request_count: 0, latency_ms: { p50: 0, p90: 0, p99: 0 } })
    expect(toLatencyPoints([empty, step(1)])).toEqual([
      { target: 100, p50: 10, p90: 20, p99: 30 },
      { target: 200, p50: null, p90: null, p99: null },
    ])
  })

  it('ось задержки линейная от нуля и вмещает p99 выше порога', () => {
    const points = toLatencyPoints([step(1, { latency_ms: { p50: 3, p90: 20, p99: 900 } })])
    expect(latencyAxis(points).domain).toEqual([0, 1000])
  })
})

describe('firstSkippedStep', () => {
  it('первая ступень с пропусками', () => {
    expect(firstSkippedStep([step(1), step(2, { skipped_count: 5 }), step(3, { skipped_count: 9 })])).toBe(2)
  })

  it('без пропусков и без поля skipped_count', () => {
    expect(firstSkippedStep([step(1, { skipped_count: 0 }), step(2)])).toBeNull()
  })
})

describe('progress', () => {
  // Три ступени по 10 с
  const scenarioSteps = [100, 200, 300].map((target_rps) => ({ target_rps, duration_seconds: 10 }))
  const start = Date.parse('2026-10-09T12:00:00Z')
  const running = (steps: StepResult[] = []): Run => ({
    ...demoRun,
    status: 'running',
    scenario: { ...demoRun.scenario, steps: scenarioSteps },
    started_at: new Date(start).toISOString(),
    steps,
  })

  it('ступень и время по часам', () => {
    expect(progress(running(), start + 15_000)).toEqual({
      elapsedSeconds: 15, totalSeconds: 30, fraction: 0.5, step: 2, stepElapsedSeconds: 5,
    })
  })

  it('не отстаёт от пришедших ступеней', () => {
    expect(progress(running([step(1), step(2)]), start + 5_000)).toMatchObject({ elapsedSeconds: 20, step: 3 })
  })

  it('время вышло, а последняя ступень не пришла: остаётся на последней', () => {
    expect(progress(running([step(1), step(2)]), start + 60_000))
      .toMatchObject({ elapsedSeconds: 30, fraction: 1, step: 3, stepElapsedSeconds: 10 })
  })

  it('до старта и после конца прогресса нет', () => {
    expect(progress({ ...running(), status: 'created', started_at: null }, start).step).toBeNull()
    expect(progress({ ...running(), status: 'completed' }, start).step).toBeNull()
  })
})

describe('headline по статусу', () => {
  it('сбой: предел не посчитан, даже если analyzer что-то вернул', () => {
    expect(headline({ ...demoRun, status: 'failed' })).toMatchObject({ kind: 'none', value: null })
  })

  it('остановленный до первой полной ступени и с нижней оценкой', () => {
    expect(headline({ ...demoRun, status: 'aborted', steps: [], analysis: null }).caption).toContain('полной ступени')
    const aborted = demoRuns['demo-aborted']()
    expect(headline(aborted)).toMatchObject({ kind: 'lower-bound', value: 200 })
    expect(headline(aborted).caption).toContain('остановлен')
  })
})

describe('formatClock', () => {
  it.each([[0, '0:00'], [65.9, '1:05'], [315, '5:15']])('%d -> %s', (seconds, text) => {
    expect(formatClock(seconds)).toBe(text)
  })
})
