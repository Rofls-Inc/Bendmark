import type { FieldErrors } from '../api/client'
import type { Scenario, Step, Target } from '../api/types'

// Форма сценария: ступени задаются диапазоном «от / до / шаг», как в макете
export interface ScenarioForm {
  name: string
  url: string
  method: string
  // Заголовки по одному на строку: «Имя: значение»
  headers: string
  body: string
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
  headers: '',
  body: '',
  fromRps: '100',
  toRps: '400',
  stepRps: '50',
  durationSeconds: '45',
}

// Методы и пределы как в ScenarioValidator координатора
export const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'] as const
export const MAX_HEADERS = 32
export const MAX_HEADER_BYTES = 8 * 1024
export const MAX_BODY_BYTES = 64 * 1024

// У GET и DELETE тела нет: поле тела для них скрыто и не отправляется
export function methodHasBody(method: string): boolean {
  return method === 'POST' || method === 'PUT' || method === 'PATCH'
}

export type RequestResult =
  | { ok: true; headers: Record<string, string>; body: string }
  | { ok: false; errors: FormErrors }

const TOKEN = /^[A-Za-z0-9!#$%&'*+\-.^_`|~]+$/
const RESERVED = ['content-length', 'transfer-encoding']

// Разбирает заголовки и тело так же строго, как координатор, чтобы ошибка была видна до запуска
export function buildRequest(form: ScenarioForm): RequestResult {
  const errors: FormErrors = {}
  const entries: [string, string][] = []
  const seen = new Set<string>()
  let bytes = 0

  const lines = form.headers.split(/\r?\n/)
  for (let i = 0; i < lines.length && !errors.headers; i++) {
    const line = lines[i].trim()
    if (line === '')
      continue
    const at = `Строка ${i + 1}: `
    const colon = line.indexOf(':')
    const name = colon < 0 ? line : line.slice(0, colon).trim()
    const value = colon < 0 ? '' : line.slice(colon + 1).trim()
    if (colon < 0)
      errors.headers = `${at}нужен формат «Имя: значение»`
    else if (!TOKEN.test(name))
      errors.headers = `${at}имя заголовка — латиница, цифры и !#$%&'*+-.^_\`|~, без пробелов`
    else if (RESERVED.includes(name.toLowerCase()))
      errors.headers = `${at}${name} агент выставляет сам`
    else if (seen.has(name.toLowerCase()))
      errors.headers = `${at}${name} уже есть`
    else {
      seen.add(name.toLowerCase())
      entries.push([name, value])
      bytes += utf8Length(name) + utf8Length(value)
    }
  }
  if (!errors.headers && seen.size > MAX_HEADERS)
    errors.headers = `Больше ${MAX_HEADERS} заголовков`
  else if (!errors.headers && bytes > MAX_HEADER_BYTES)
    errors.headers = `Заголовки больше ${MAX_HEADER_BYTES / 1024} КиБ`

  const body = methodHasBody(form.method) ? form.body : ''
  if (body !== '') {
    if (utf8Length(body) > MAX_BODY_BYTES)
      errors.body = `Тело больше ${MAX_BODY_BYTES / 1024} КиБ`
    if (!errors.headers && !seen.has('content-type'))
      errors.headers = 'Для тела нужен заголовок Content-Type, например application/json'
  }

  // fromEntries, а не присваивание: имя __proto__ стало бы прототипом, а не заголовком
  return Object.keys(errors).length > 0
    ? { ok: false, errors }
    : { ok: true, headers: Object.fromEntries(entries), body }
}

function utf8Length(text: string): number {
  return new TextEncoder().encode(text).length
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
    return { ok: false, errors: { stepRps: `Больше ${MAX_STEPS} ступеней, увеличьте шаг` } }

  const steps = Array.from({ length: count }, (_, i) => ({
    target_rps: round(from + i * step),
    duration_seconds: duration,
  }))
  // Слишком мелкий шаг после округления даёт повторы, а координатор требует строгого роста
  if (steps.some((s, i) => i > 0 && s.target_rps <= steps[i - 1].target_rps))
    return { ok: false, errors: { stepRps: 'Шаг слишком мелкий: после округления нагрузка не растёт' } }
  return { ok: true, steps }
}

// Предупреждения не блокируют запуск, в отличие от ошибок
export type FormWarnings = Partial<Record<FormField, string>>

const SHORT_STEP_SECONDS = 10
const LONG_RUN_SECONDS = 30 * 60

export function formWarnings(form: ScenarioForm, steps: Step[] | null): FormWarnings {
  const warnings: FormWarnings = {}
  if (isLocalUrl(form.url))
    warnings.url = 'localhost здесь — это сам агент, а не ваш компьютер: укажите адрес, доступный агенту'
  const duration = Number(form.durationSeconds.replace(',', '.'))
  if (Number.isFinite(duration) && duration > 0 && duration < SHORT_STEP_SECONDS)
    warnings.durationSeconds = `Ступень короче ${SHORT_STEP_SECONDS} с: перцентили будут шумными`
  else if (steps && totalSeconds(steps) > LONG_RUN_SECONDS)
    warnings.durationSeconds = 'Прогон дольше 30 минут'
  return warnings
}

function isLocalUrl(raw: string): boolean {
  try {
    const host = new URL(raw.trim()).hostname
    return host === 'localhost' || host === '127.0.0.1' || host === '[::1]'
  } catch {
    return false
  }
}

// Заголовки и тело попадают в сценарий, только если заданы: GET-сценарий выглядит как раньше
export function toScenario(form: ScenarioForm, steps: Step[]): Scenario {
  const target: Target = { url: form.url.trim(), method: form.method }
  const request = buildRequest(form)
  if (request.ok && Object.keys(request.headers).length > 0)
    target.headers = request.headers
  if (request.ok && request.body !== '')
    target.body = request.body
  return { name: form.name.trim(), target, steps }
}

// Обратная операция к buildSteps: форма по готовому сценарию (кнопка «Повторить»).
// Если ступени не равномерный рост с одной длительностью, форма их точно не повторит:
// берём первую и последнюю ступень и честно говорим об этом.
export function formFromScenario(scenario: Scenario): { form: ScenarioForm; exact: boolean } {
  const steps = scenario.steps
  const first = steps[0]
  const last = steps.at(-1)
  const { target } = scenario
  const base = {
    ...defaultForm,
    name: scenario.name,
    url: target.url,
    method: target.method,
    headers: Object.entries(target.headers ?? {}).map(([name, value]) => `${name}: ${value}`).join('\n'),
    body: target.body ?? '',
  }
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
    else if (key === 'target.body')
      put('body', message)
    else if (key === 'target.headers')
      put('headers', message)
    else if (key.startsWith('target.headers.'))
      put('headers', `${key.slice('target.headers.'.length)}: ${message}`)
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

// До трёх знаков: 0.1 + 0.2 не должно уйти координатору как 0.30000000000000004
function round(value: number): number {
  return Math.round(value * 1000) / 1000
}
