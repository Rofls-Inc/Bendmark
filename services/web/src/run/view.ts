import { FINAL_STATUSES, type Analysis, type Run, type StepResult } from '../api/types'

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

// Опрос идёт, пока прогон не завершён и пока анализатор не ответил:
// координатор сначала ставит конечный статус с analysis.status = pending, потом записывает предел
export function needsPolling(run: Run): boolean {
  return !FINAL_STATUSES.includes(run.status) || run.analysis?.status === 'pending'
}

export type StepVerdict = 'ok' | 'limit' | 'failed' | 'after' | 'not-analyzed'

// Есть ли у анализа подписи ступеней: только когда анализатор ответил
export function hasVerdicts(analysis: Analysis | null): analysis is Analysis {
  return analysis?.status === 'found' || analysis?.status === 'not_found'
}

// Как подписать ступень в отчёте: «норма», «предел» (последняя без отказа), «отказ»,
// после отказа, «не анализ.» — ступени, которые не ушли анализатору (после пропусков или остановки)
export function stepVerdict(step: StepResult, analysis: Analysis | null): StepVerdict | null {
  if (!hasVerdicts(analysis))
    return null
  const failed = analysis.failed_step_index
  if (failed !== null && step.index > failed)
    return 'after'
  if (failed !== null && step.index === failed)
    return 'failed'
  if (step.index > analysis.analyzed_steps)
    return 'not-analyzed'
  if (analysis.status === 'found' && failed !== null && step.index === failed - 1)
    return 'limit'
  return 'ok'
}

export interface Headline {
  // Что показать крупно: предел, нижняя оценка или ничего
  value: number | null
  kind: 'limit' | 'lower-bound' | 'below-first' | 'pending' | 'none'
  caption: string
}

export function headline(run: Run): Headline {
  const analysis = run.analysis
  if (!analysis)
    return {
      value: null,
      kind: 'none',
      caption: run.status === 'failed'
        ? 'Прогон завершился сбоем, предел не ищется'
        : run.steps.length === 0 ? 'Нет ни одной полной ступени' : 'Предела нет',
    }
  switch (analysis.status) {
    case 'pending':
      return { value: null, kind: 'pending', caption: analysis.message }
    case 'found':
      return analysis.limit_rps === null
        ? { value: null, kind: 'below-first', caption: analysis.message }
        : { value: analysis.limit_rps, kind: 'limit', caption: analysis.message }
    case 'not_found':
      return { value: analysis.limit_rps, kind: 'lower-bound', caption: analysis.message }
    case 'undetermined':
    case 'error':
      return { value: null, kind: 'none', caption: analysis.message }
  }
}

// Номер ступени, которая идёт сейчас: следующая после последней пришедшей
export function currentStep(run: Run): number | null {
  if (run.status !== 'running')
    return null
  const done = run.steps.length
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
