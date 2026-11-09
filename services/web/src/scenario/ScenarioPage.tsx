import { useState, type FormEvent, type ReactNode } from 'react'
import { useLocation, useNavigate } from 'react-router'
import { ValidationError, createRun } from '../api/client'
import type { Scenario } from '../api/types'
import { State } from '../components/State'
import { refreshRecent } from '../run/recent'
import { formatDuration, formatNumber } from '../run/view'
import {
  buildSteps,
  defaultForm,
  formFromScenario,
  formWarnings,
  mapServerErrors,
  toScenario,
  totalSeconds,
  type FormErrors,
  type FormField,
  type FormWarnings,
  type ScenarioForm,
} from './form'

export function ScenarioPage() {
  const navigate = useNavigate()
  const location = useLocation()
  // «Повторить» из отчёта передаёт сценарий прогона
  const [initial] = useState(() => {
    const scenario = (location.state as { scenario?: Scenario } | null)?.scenario
    return scenario ? formFromScenario(scenario) : null
  })
  const [form, setForm] = useState<ScenarioForm>(initial?.form ?? defaultForm)
  const [serverErrors, setServerErrors] = useState<FormErrors>({})
  const [submitting, setSubmitting] = useState(false)

  const built = buildSteps(form)
  const errors: FormErrors = { ...serverErrors, ...(built.ok ? {} : built.errors) }
  const warnings: FormWarnings = formWarnings(form, built.ok ? built.steps : null)
  const warn = (field: FormField) => (errors[field] ? undefined : warnings[field])

  const set = (field: FormField) => (value: string) => {
    setForm((prev) => ({ ...prev, [field]: value }))
    // Ошибка координатора относится к старому значению поля
    setServerErrors((prev) => ({ ...prev, [field]: undefined, form: undefined }))
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!built.ok || submitting)
      return
    setSubmitting(true)
    setServerErrors({})
    try {
      const scenario = toScenario(form, built.steps)
      const run = await createRun(scenario)
      refreshRecent()
      navigate(`/runs/${run.id}`)
    } catch (err) {
      setServerErrors(err instanceof ValidationError
        ? mapServerErrors(err.fields)
        : { form: err instanceof Error ? err.message : 'Не удалось создать прогон' })
      setSubmitting(false)
    }
  }

  return (
    <form className="page" onSubmit={submit} noValidate>
      <header className="page-header">
        <div>
          <p className="eyebrow">Новый прогон</p>
          <h1>{form.name.trim() || 'Новый сценарий'}</h1>
        </div>
        <div className="actions">
          <button type="submit" className="primary" disabled={!built.ok || submitting}>
            {submitting ? 'Запускаем…' : 'Запустить'}
          </button>
        </div>
      </header>

      {errors.form && <p className="alert" role="alert">{errors.form}</p>}
      {initial && !initial.exact && (
        <p className="note">
          Ступени прошлого прогона не укладываются в «от / до / шаг»: форма заполнена по первой и последней ступени.
        </p>
      )}

      <div className="grid">
        <section className="panel">
          <div className="panel-section">
            <h2>Цель</h2>
            <Field label="Имя сценария" error={errors.name}>
              <input value={form.name} onChange={(e) => set('name')(e.target.value)} />
            </Field>
            <div className="row target">
              <Field label="Метод" error={errors.method}>
                {/* Агент пока умеет только GET */}
                <select value={form.method} onChange={(e) => set('method')(e.target.value)}>
                  <option value="GET">GET</option>
                </select>
              </Field>
              <Field
                label="URL"
                error={errors.url}
                warning={warn('url')}
                hint="Адрес должен быть доступен агенту, а не браузеру"
              >
                <input value={form.url} onChange={(e) => set('url')(e.target.value)} spellCheck={false} />
              </Field>
            </div>
          </div>

          <div className="panel-section">
            <div className="panel-head">
              <h2>Нагрузка</h2>
              <p className="hint">открытая модель · ступени растут от «от» до «до» с шагом</p>
            </div>
            <div className="row">
              <Field label="От" unit="запр/с" error={errors.fromRps}>
                <input inputMode="decimal" value={form.fromRps} onChange={(e) => set('fromRps')(e.target.value)} />
              </Field>
              <Field label="До" unit="запр/с" error={errors.toRps}>
                <input inputMode="decimal" value={form.toRps} onChange={(e) => set('toRps')(e.target.value)} />
              </Field>
              <Field label="Шаг" unit="запр/с" error={errors.stepRps}>
                <input inputMode="decimal" value={form.stepRps} onChange={(e) => set('stepRps')(e.target.value)} />
              </Field>
              <Field label="Ступень" unit="с" error={errors.durationSeconds} warning={warn('durationSeconds')}>
                <input
                  inputMode="decimal"
                  value={form.durationSeconds}
                  onChange={(e) => set('durationSeconds')(e.target.value)}
                />
              </Field>
            </div>
          </div>
        </section>

        <section className="panel">
          <div className="panel-head">
            <h2>План прогона</h2>
            {built.ok && <span className="hint">{built.steps.length} ступеней</span>}
          </div>
          {built.ok
            ? <Plan steps={built.steps} url={form.url} />
            : <State kind="empty" title="План не построен"><p className="muted">Исправьте ошибки в форме.</p></State>}
        </section>
      </div>
    </form>
  )
}

function Plan({ steps, url }: { steps: { target_rps: number; duration_seconds: number }[]; url: string }) {
  const max = Math.max(...steps.map((s) => s.target_rps))
  const requests = steps.reduce((sum, s) => sum + s.target_rps * s.duration_seconds, 0)

  return (
    <>
      <div>
        <p className="big">{formatDuration(totalSeconds(steps))}</p>
        <p className="label">расчётная длительность</p>
      </div>
      <div className="bars">
        {steps.map((s, i) => (
          <div key={i} className="bar-col">
            <div className="bar-track">
              <div className="bar" style={{ height: `${(s.target_rps / max) * 100}%` }} />
            </div>
            <span>{formatNumber(s.target_rps)}</span>
          </div>
        ))}
      </div>
      <dl className="facts">
        <dt>Цель</dt>
        <dd>{url.trim() || '—'}</dd>
        <dt>Ступеней</dt>
        <dd>{steps.length}</dd>
        <dt>Запросов всего</dt>
        <dd>{formatNumber(requests)}</dd>
        <dt>Пиковая нагрузка</dt>
        <dd>{formatNumber(max)} запр/с</dd>
      </dl>
    </>
  )
}

interface FieldProps {
  label: string
  unit?: string
  hint?: string
  error?: string
  // Не блокирует запуск; показывается, только если нет ошибки
  warning?: string
  children: ReactNode
}

function Field({ label, unit, hint, error, warning, children }: FieldProps) {
  const state = error ? ' invalid' : warning ? ' warning' : ''
  return (
    <label className={`field${state}`}>
      <span className="label">{label}</span>
      <span className="control">
        {children}
        {unit && <span className="unit">{unit}</span>}
      </span>
      {error
        ? <span className="error">{error}</span>
        : warning
          ? <span className="warning-text">{warning}</span>
          : hint && <span className="hint">{hint}</span>}
    </label>
  )
}
