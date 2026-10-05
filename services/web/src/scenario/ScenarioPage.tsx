import { useState, type FormEvent, type ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { ValidationError, createRun } from '../api/client'
import { formatDuration, formatNumber } from '../run/view'
import {
  buildSteps,
  defaultForm,
  mapServerErrors,
  toScenario,
  totalSeconds,
  type FormErrors,
  type FormField,
  type ScenarioForm,
} from './form'

export function ScenarioPage() {
  const navigate = useNavigate()
  const [form, setForm] = useState<ScenarioForm>(defaultForm)
  const [serverErrors, setServerErrors] = useState<FormErrors>({})
  const [submitting, setSubmitting] = useState(false)

  const built = buildSteps(form)
  const errors: FormErrors = { ...serverErrors, ...(built.ok ? {} : built.errors) }

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
      const run = await createRun(toScenario(form, built.steps))
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
          <h1>{form.name.trim() || 'Новый сценарий'}</h1>
          <p className="muted">сценарий · честный замер</p>
        </div>
        <button type="submit" className="primary" disabled={!built.ok || submitting}>
          {submitting ? 'Запускаем…' : 'Запустить'}
        </button>
      </header>

      {errors.form && <p className="alert">{errors.form}</p>}

      <div className="grid-2">
        <section className="panel">
          <h2>Цель</h2>
          <Field label="Имя сценария" error={errors.name}>
            <input value={form.name} onChange={(e) => set('name')(e.target.value)} />
          </Field>
          <div className="row">
            <Field label="Метод" error={errors.method} narrow>
              {/* Агент пока умеет только GET */}
              <select value={form.method} onChange={(e) => set('method')(e.target.value)}>
                <option value="GET">GET</option>
              </select>
            </Field>
            <Field label="URL" error={errors.url} hint="Адрес должен быть доступен агенту, а не браузеру">
              <input value={form.url} onChange={(e) => set('url')(e.target.value)} spellCheck={false} />
            </Field>
          </div>

          <h2>Нагрузка</h2>
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
            <Field label="Ступень" unit="с" error={errors.durationSeconds}>
              <input
                inputMode="decimal"
                value={form.durationSeconds}
                onChange={(e) => set('durationSeconds')(e.target.value)}
              />
            </Field>
          </div>
        </section>

        <section className="panel">
          <h2>План прогона</h2>
          {built.ok ? <Plan steps={built.steps} /> : <p className="muted">Исправьте ошибки в форме</p>}
        </section>
      </div>
    </form>
  )
}

function Plan({ steps }: { steps: { target_rps: number; duration_seconds: number }[] }) {
  const max = Math.max(...steps.map((s) => s.target_rps))
  const requests = steps.reduce((sum, s) => sum + s.target_rps * s.duration_seconds, 0)

  return (
    <>
      <p className="big">{formatDuration(totalSeconds(steps))}</p>
      <p className="label">расчётная длительность</p>
      <div className="bars">
        {steps.map((s, i) => (
          <div key={i} className="bar-col">
            <div className="bar" style={{ height: `${(s.target_rps / max) * 100}%` }} />
            <span>{formatNumber(s.target_rps)}</span>
          </div>
        ))}
      </div>
      <dl className="facts">
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
  narrow?: boolean
  children: ReactNode
}

function Field({ label, unit, hint, error, narrow, children }: FieldProps) {
  return (
    <label className={`field${narrow ? ' narrow' : ''}${error ? ' invalid' : ''}`}>
      <span className="label">{label}</span>
      <span className="control">
        {children}
        {unit && <span className="unit">{unit}</span>}
      </span>
      {error ? <span className="error">{error}</span> : hint && <span className="hint">{hint}</span>}
    </label>
  )
}
