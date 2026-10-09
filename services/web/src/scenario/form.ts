import type { FieldErrors } from '../api/client'
import type { Scenario, Step } from '../api/types'

// Форма сценария: ступени задаются диапазоном «от / до / шаг», как в макете
export interface ScenarioForm {
  name: string
  url: string
  method: string
  fromRps: string
  toRps: string
  stepRps: string
  durationSeconds: string
}

export type FormField = keyof ScenarioForm

// Ошибки, привязанные к полям формы. Ключ 'form' - общая ошибка без поля.
export type FormErrors = Partial<Record<FormField | 'form', string>>

export const MAX_STEPS = 50

export const defaultForm: ScenarioForm = {
  name: 'abstock-home-ramp',
  url: 'http://abstock:8080/',
  method: 'GET',
  fromRps: '100',
  toRps: '400',
  stepRps: '50',
  durationSeconds: '45',
}

export type BuildResult =
  | { ok: true; steps: Step[] }
  | { ok: false; errors: FormErrors }

// Собирает ступени from, from + step, ... не выше to.
// Нагрузка строго растёт, как требует правило сценария из docs/domain.md.
export function buildSteps(form: ScenarioForm): BuildResult {
  const errors: FormErrors = {}
  const from = parsePositive(form.fromRps, 'fromRps', errors)
  const to = parsePositive(form.toRps, 'toRps', errors)
  const step = parsePositive(form.stepRps, 'stepRps', errors)
  const duration = parsePositive(form.durationSeconds, 'durationSeconds', errors)

  if (from !== null && to !== null && to < from)
    errors.toRps = 'Не меньше начальной нагрузки'
  if (Object.keys(errors).length > 0 || from === null || to === null || step === null || duration === null)
    return { ok: false, errors }

  // Считаем через индекс, а не прибавлением, чтобы не копить погрешность дробного шага
  const count = Math.floor((to - from) / step + 1e-9) + 1
  if (count > MAX_STEPS)
    return { ok: false, errors: { stepRps: `Получается ${count} ступеней, максимум ${MAX_STEPS}` } }

  const steps = Array.from({ length: count }, (_, i) => ({
    target_rps: round(from + i * step),
    duration_seconds: duration,
  }))
  return { ok: true, steps }
}

export function toScenario(form: ScenarioForm, steps: Step[]): Scenario {
  return {
    name: form.name.trim(),
    target: { url: form.url.trim(), method: form.method },
    steps,
  }
}

// Обратная операция к buildSteps: форма по готовому сценарию (кнопка «Повторить»).
// Если ступени не равномерный рост с одной длительностью, форма их точно не повторит:
// берём первую и последнюю ступень и честно говорим об этом.
export function formFromScenario(scenario: Scenario): { form: ScenarioForm; exact: boolean } {
  const steps = scenario.steps
  const first = steps[0]
  const last = steps.at(-1)
  const base = { ...defaultForm, name: scenario.name, url: scenario.target.url, method: scenario.target.method }
  if (!first || !last)
    return { form: base, exact: false }

  const step = steps.length > 1 ? steps[1].target_rps - first.target_rps : 0
  const form: ScenarioForm = {
    ...base,
    fromRps: String(first.target_rps),
    toRps: String(last.target_rps),
    stepRps: String(step > 0 ? round(step) : defaultForm.stepRps),
    durationSeconds: String(first.duration_seconds),
  }

  const rebuilt = buildSteps(form)
  const exact = rebuilt.ok
    && rebuilt.steps.length === steps.length
    && rebuilt.steps.every((s, i) =>
      Math.abs(s.target_rps - steps[i].target_rps) < 1e-6 && s.duration_seconds === steps[i].duration_seconds)
  return { form, exact }
}

export function totalSeconds(steps: Step[]): number {
  return steps.reduce((sum, s) => sum + s.duration_seconds, 0)
}

// Раскладывает ошибки координатора по полям формы.
// Ошибки ступеней (steps[i].target_rps) относятся к диапазону нагрузки, длительность - к полю длительности.
export function mapServerErrors(fields: FieldErrors): FormErrors {
  const errors: FormErrors = {}
  const put = (field: FormField | 'form', message: string) => {
    errors[field] ??= message
  }

  for (const [key, messages] of Object.entries(fields)) {
    const message = messages[0] ?? 'Некорректное значение'
    if (key === 'name')
      put('name', message)
    else if (key === 'target.url')
      put('url', message)
    else if (key === 'target.method')
      put('method', message)
    else if (/^steps\[\d+\]\.duration_seconds$/.test(key))
      put('durationSeconds', message)
    else if (/^steps(\[\d+\](\.target_rps)?)?$/.test(key))
      put('stepRps', `${stepLabel(key)}${message}`)
    else
      put('form', `${key}: ${message}`)
  }
  return errors
}

function stepLabel(key: string): string {
  const match = /^steps\[(\d+)\]/.exec(key)
  return match ? `Ступень ${Number(match[1]) + 1}: ` : ''
}

function parsePositive(raw: string, field: FormField, errors: FormErrors): number | null {
  const value = Number(raw.replace(',', '.').trim())
  if (raw.trim() === '' || !Number.isFinite(value) || value <= 0) {
    errors[field] = 'Нужно число больше нуля'
    return null
  }
  return value
}

function round(value: number): number {
  return Math.round(value * 1000) / 1000
}
