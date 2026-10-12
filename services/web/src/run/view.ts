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
  return logAxis(points.map((p) => p.p99), threshold)
}

// То же для всех трёх перцентилей сразу
export function latencyAxis(points: LatencyPoint[], threshold = P99_THRESHOLD_MS): Axis {
  return logAxis(points.flatMap((p) => [p.p50, p.p90, p.p99]), threshold)
}

function logAxis(raw: (number | null)[], threshold: number): Axis {
  const values = raw.filter((v): v is number => v !== null)
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
  if (run.status === 'failed')
    return { value: null, kind: 'none', caption: 'Прогон завершился сбоем, предел не ищется' }
  if (!analysis)
    return {
      value: null,
      kind: 'none',
      caption: run.steps.length === 0 ? 'Нет ни одной полной ступени' : 'Предела нет',
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

export interface LatencyPoint {
  target: number
  // null, если на ступени не было измерений: на логарифмической оси ноль не нарисовать
  p50: number | null
  p90: number | null
  p99: number | null
}

export function toLatencyPoints(steps: StepResult[]): LatencyPoint[] {
  const positive = (value: number, s: StepResult) => (s.request_count > 0 && value > 0 ? value : null)
  return [...steps]
    .sort((a, b) => a.index - b.index)
    .map((s) => ({
      target: s.target_rps,
      p50: positive(s.latency_ms.p50, s),
      p90: positive(s.latency_ms.p90, s),
      p99: positive(s.latency_ms.p99, s),
    }))
}

// Номер первой ступени, где агент пропускал запросы. Анализатор берёт только ступени до неё.
export function firstSkippedStep(steps: StepResult[]): number | null {
  const skipped = steps.filter((s) => s.skipped_count > 0).map((s) => s.index)
  return skipped.length > 0 ? Math.min(...skipped) : null
}

export interface Progress {
  elapsedSeconds: number
  totalSeconds: number
  // 0..1 для полосы прогресса
  fraction: number
  // Номер ступени по времени (с единицы), null до старта
  step: number | null
  stepElapsedSeconds: number
}

// Где прогон сейчас по часам. Агент присылает ступень только после её конца,
// поэтому текущую ступень считаем по времени от started_at.
export function progress(run: Run, now: number): Progress {
  const durations = run.scenario.steps.map((s) => s.duration_seconds)
  const totalSeconds = durations.reduce((sum, d) => sum + d, 0)
  const started = run.started_at ? Date.parse(run.started_at) : NaN
  if (run.status !== 'running' || Number.isNaN(started))
    return { elapsedSeconds: 0, totalSeconds, fraction: 0, step: null, stepElapsedSeconds: 0 }

  // Пришедшие ступени точно закончились: время не отстаёт от них, даже если часы браузера спешат назад
  const done = durations.slice(0, run.steps.length).reduce((sum, d) => sum + d, 0)
  const elapsed = Math.max(done, Math.min(totalSeconds, Math.max(0, (now - started) / 1000)))

  // Время вышло, а последняя ступень ещё не пришла: остаёмся на ней, а не «после конца»
  let step = durations.length
  let stepElapsed = durations.at(-1) ?? 0
  let rest = elapsed
  for (let i = 0; i < durations.length; i++) {
    if (rest < durations[i]) {
      step = i + 1
      stepElapsed = rest
      break
    }
    rest -= durations[i]
  }

  return {
    elapsedSeconds: elapsed,
    totalSeconds,
    fraction: totalSeconds > 0 ? elapsed / totalSeconds : 0,
    step,
    stepElapsedSeconds: stepElapsed,
  }
}

// 201 -> «3:21»
export function formatClock(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds))
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`
}
