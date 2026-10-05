import type { Limit, Run, StepResult } from '../api/types'

// Порог p99 по умолчанию, как у анализатора (--p99-ms)
export const P99_THRESHOLD_MS = 500

export interface ChartPoint {
  target: number
  throughput: number
  // null, если на ступени не было измерений: на логарифмической оси ноль не нарисовать
  p99: number | null
}

export function toChartPoints(steps: StepResult[]): ChartPoint[] {
  return [...steps]
    .sort((a, b) => a.index - b.index)
    .map((s) => ({
      target: s.target_rps,
      throughput: s.throughput_rps,
      p99: s.request_count > 0 && s.latency_ms.p99 > 0 ? s.latency_ms.p99 : null,
    }))
}

export interface Axis {
  domain: [number, number]
  ticks: number[]
}

// Ось нагрузки от нуля до круглого значения с запасом над максимумом
export function rpsAxis(points: ChartPoint[]): Axis {
  const max = Math.max(1, ...points.map((p) => Math.max(p.target, p.throughput)))
  const step = niceStep(max / 4)
  const top = Math.ceil((max * 1.05) / step) * step
  return { domain: [0, top], ticks: Array.from({ length: Math.round(top / step) + 1 }, (_, i) => i * step) }
}

// Логарифмическая ось p99 по декадам с делениями 1-2-5. Порог всегда попадает на ось.
export function p99Axis(points: ChartPoint[], threshold = P99_THRESHOLD_MS): Axis {
  const values = points.map((p) => p.p99).filter((v): v is number => v !== null)
  const lo = 10 ** Math.floor(Math.log10(Math.min(threshold, ...values)))
  const hi = 10 ** Math.ceil(Math.log10(Math.max(threshold, ...values) * 1.05))
  const ticks: number[] = []
  for (let decade = lo; decade < hi; decade *= 10)
    ticks.push(decade, decade * 2, decade * 5)
  ticks.push(hi)
  return { domain: [lo, hi], ticks }
}

function niceStep(raw: number): number {
  const magnitude = 10 ** Math.floor(Math.log10(raw))
  const normalized = raw / magnitude
  const nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10
  return nice * magnitude
}

export type StepVerdict = 'ok' | 'limit' | 'failed' | 'after'

// Как подписать ступень в отчёте: «норма», «предел» (последняя без отказа), «отказ», после отказа
export function stepVerdict(step: StepResult, limit: Limit | null | undefined): StepVerdict | null {
  if (!limit)
    return null
  const failed = limit.failed_step_index ?? null
  if (failed !== null && step.index > failed)
    return 'after'
  if (failed !== null && step.index === failed)
    return 'failed'
  if (limit.found && failed !== null && step.index === failed - 1)
    return 'limit'
  return 'ok'
}

export interface Headline {
  // Что показать крупно: предел, нижняя оценка или ничего
  value: number | null
  kind: 'limit' | 'lower-bound' | 'below-first' | 'none'
  caption: string
}

export function headline(run: Run): Headline {
  const limit = run.limit
  if (!limit)
    return { value: null, kind: 'none', caption: 'Предел ещё не посчитан' }
  if (!limit.found)
    return {
      value: limit.limit_rps ?? null,
      kind: 'lower-bound',
      caption: 'Предел не найден: сервис выдержал все ступени, держит не меньше',
    }
  if (limit.limit_rps == null)
    return { value: null, kind: 'below-first', caption: 'Предел ниже первой ступени' }
  return { value: limit.limit_rps, kind: 'limit', caption: 'Предел сервиса' }
}

// Номер ступени, которая идёт сейчас: следующая после последней пришедшей
export function currentStep(run: Run): number | null {
  if (run.status !== 'running')
    return null
  const done = run.steps?.length ?? 0
  return Math.min(done + 1, run.scenario.steps.length)
}

export function formatDuration(seconds: number): string {
  const total = Math.round(seconds)
  const minutes = Math.floor(total / 60)
  const rest = total % 60
  if (minutes === 0)
    return `${rest} с`
  return rest === 0 ? `${minutes} мин` : `${minutes} мин ${rest} с`
}

export function formatNumber(value: number, digits = 0): string {
  return value.toLocaleString('ru-RU', { maximumFractionDigits: digits })
}
