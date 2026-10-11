import { describe, expect, it } from 'vitest'
import type { Analysis, Run, StepResult } from '../api/types'
import { demoRun } from '../demo/demoRun'
import {
  currentStep,
  formatDuration,
  headline,
  needsPolling,
  p99Axis,
  rpsAxis,
  stepVerdict,
  toChartPoints,
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

  it('предел ниже первой ступени', () => {
    expect(headline(run(analysis({ failed_step_index: 1, reasons: ['x'] }))))
      .toMatchObject({ kind: 'below-first', value: null })
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

  it('ось p99 по декадам и с порогом', () => {
    expect(p99Axis(points)).toEqual({ domain: [10, 1000], ticks: [10, 20, 50, 100, 200, 500, 1000] })
  })

  it('порог попадает на ось, даже если все p99 намного ниже', () => {
    const fast = toChartPoints([step(1, { latency_ms: { p50: 1, p90: 2, p99: 3 } })])
    expect(p99Axis(fast).domain).toEqual([1, 1000])
  })

  it('без измерений p99 ось строится по порогу', () => {
    expect(p99Axis([]).domain).toEqual([100, 1000])
  })
})
