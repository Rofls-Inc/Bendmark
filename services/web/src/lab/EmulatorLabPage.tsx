import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { createRun, getEmulatorConfiguration, updateEmulatorConfiguration } from '../api/client'
import type { EmulatorConfiguration, EmulatorSettings, Scenario } from '../api/types'
import { Field } from '../components/Field'
import { State } from '../components/State'
import { refreshRecent } from '../run/recent'
import { formatNumber } from '../run/view'

const modes = [
  { value: 'healthy', name: 'Норма', description: 'Обрабатывает запросы без ограничения размером пула.' },
  { value: 'reject', name: 'Отказы', description: 'Отвечает 503, когда все обработчики заняты.' },
  { value: 'saturation', name: 'Насыщение', description: 'Запросы ждут в очереди, пока освободится обработчик.' },
  { value: 'slow', name: 'Замедление', description: 'Запросы из очереди дополнительно задерживаются.' },
] as const

type LoadState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; configuration: EmulatorConfiguration }

export function EmulatorLabPage() {
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<LoadState>({ kind: 'loading' })

  useEffect(() => {
    const controller = new AbortController()
    void getEmulatorConfiguration(controller.signal).then(
      (configuration) => {
        if (!controller.signal.aborted)
          setState({ kind: 'ready', configuration })
      },
      (error: unknown) => {
        if (!controller.signal.aborted)
          setState({ kind: 'error', message: errorMessage(error) })
      },
    )
    return () => controller.abort()
  }, [attempt])

  return (
    <div className="page">
      <header className="page-header"><div><h1>Лаборатория</h1></div></header>
      {state.kind === 'ready' ? <EmulatorForm initial={state.configuration} />
        : state.kind === 'loading' ? <State kind="loading" title="Загружаем настройки эмулятора…" />
          : <State kind="error" title="Не удалось подключиться к эмулятору">
            <p className="muted">{state.message}</p>
            <button type="button" onClick={() => {
              setState({ kind: 'loading' })
              setAttempt((value) => value + 1)
            }}>Повторить</button>
          </State>}
    </div>
  )
}

function EmulatorForm({ initial }: { initial: EmulatorConfiguration }) {
  const navigate = useNavigate()
  const [configuration, setConfiguration] = useState(initial)
  const [draft, setDraft] = useState(initial.settings)
  const [pending, setPending] = useState<'saving' | 'starting' | null>(null)
  const [error, setError] = useState('')
  const [saved, setSaved] = useState(false)
  const changed = Object.keys(draft).some((key) => {
    const field = key as keyof EmulatorSettings
    return draft[field] !== configuration.settings[field]
  })
  const mode = modes.find((option) => option.value === configuration.settings.mode)!
  const scenario = demoScenario(configuration)
  const invalidCounts = [draft.workers, draft.queue_size].some((n) => !Number.isSafeInteger(n) || n < 1)

  const set = <FieldName extends keyof EmulatorSettings>(field: FieldName, value: EmulatorSettings[FieldName]) => {
    setDraft((current) => ({ ...current, [field]: value }))
    setError('')
    setSaved(false)
  }

  const apply = async (event: FormEvent) => {
    event.preventDefault()
    if (pending || !changed || invalidCounts)
      return
    setPending('saving')
    setError('')
    try {
      const applied = await updateEmulatorConfiguration(draft)
      setConfiguration(applied)
      setDraft(applied.settings)
      setSaved(true)
    } catch (error: unknown) {
      setError(errorMessage(error))
    } finally {
      setPending(null)
    }
  }

  const start = async () => {
    if (pending || changed)
      return
    setPending('starting')
    setError('')
    try {
      const run = await createRun(scenario)
      refreshRecent()
      navigate(`/runs/${run.id}`)
    } catch (error: unknown) {
      setError(errorMessage(error))
      setPending(null)
    }
  }

  return (
    <>
      {error && <p className="alert" role="alert">{error}</p>}
      <div className="grid">
        <form className="panel" onSubmit={apply}>
          <div className="panel-section">
            <h2>Эмулятор</h2>
            <fieldset className="lab-modes" disabled={pending !== null}>
              <legend className="label">Режим</legend>
              {modes.map(({ value, name, description }) => (
                <label key={value} className="lab-mode">
                  <input
                    type="radio"
                    name="mode"
                    value={value}
                    checked={draft.mode === value}
                    onChange={() => set('mode', value)}
                  />
                  <span>
                    <strong>{name}</strong>
                    <small>{description}</small>
                  </span>
                </label>
              ))}
            </fieldset>
          </div>
          <div className="panel-section">
            <div className="row">
              <Field label="Время обработки" hint="Например, 250ms">
                <input required value={draft.service_time} disabled={pending !== null}
                  onChange={(event) => set('service_time', event.target.value)} />
              </Field>
              <Field label="Обработчики" hint="Для режимов с пулом">
                <input type="number" min={1} step={1} required disabled={pending !== null}
                  value={Number.isNaN(draft.workers) ? '' : draft.workers}
                  onChange={(event) => set('workers', event.target.valueAsNumber)} />
              </Field>
              <Field label="Размер очереди" hint="Для насыщения и замедления">
                <input type="number" min={1} step={1} required disabled={pending !== null}
                  value={Number.isNaN(draft.queue_size) ? '' : draft.queue_size}
                  onChange={(event) => set('queue_size', event.target.valueAsNumber)} />
              </Field>
            </div>
            {draft.mode === 'slow' && <Field label="Дополнительная задержка" hint="Например, 5s">
              <input required value={draft.slow_delay} disabled={pending !== null}
                onChange={(event) => set('slow_delay', event.target.value)} />
            </Field>}
            <details className="lab-options">
              <summary>Случайные задержки</summary>
              <div className="row">
                <Field label="Разброс" hint="Например, 20ms">
                  <input required value={draft.jitter} disabled={pending !== null}
                    onChange={(event) => set('jitter', event.target.value)} />
                </Field>
                <Field label="Вероятность выброса" hint="От 0 до 1">
                  <input type="number" min={0} max={1} step="any" required value={draft.outlier_rate}
                    disabled={pending !== null} onChange={(event) => set('outlier_rate', event.target.value)} />
                </Field>
                <Field label="Задержка при выбросе" hint="Например, 800ms">
                  <input required value={draft.outlier_delay} disabled={pending !== null}
                    onChange={(event) => set('outlier_delay', event.target.value)} />
                </Field>
                <Field label="Seed">
                  <input inputMode="numeric" required value={draft.seed} disabled={pending !== null}
                    onChange={(event) => set('seed', event.target.value)} />
                </Field>
              </div>
            </details>
            <div className="actions">
              <button type="submit" className="primary" disabled={!changed || pending !== null || invalidCounts}>
                {pending === 'saving' ? 'Применяем…' : 'Применить настройки'}
              </button>
              {saved && <span className="lab-saved" role="status">Настройки применены</span>}
            </div>
          </div>
        </form>

        <section className="panel">
          <h2>Пробный прогон</h2>
          <dl className="facts">
            <dt>Активный режим</dt>
            <dd>{mode.name}</dd>
            <dt>Цель</dt>
            <dd className="lab-url">{configuration.target_url}</dd>
            {configuration.capacity_rps > 0 && <>
              <dt>Расчётная ёмкость пула</dt>
              <dd>≈ {formatNumber(configuration.capacity_rps, 2)} запр/с</dd>
            </>}
            <dt>Ступени, запр/с</dt>
            <dd>{scenario.steps.map((step) => formatNumber(step.target_rps, 3)).join(' · ')}</dd>
            <dt>Длительность ступени</dt>
            <dd>{scenario.steps[0].duration_seconds} с</dd>
          </dl>
          {changed && <p className="hint">Примени изменения, чтобы запустить прогон с новыми настройками.</p>}
          <div className="actions">
            <button className="primary" type="button" disabled={changed || pending !== null} onClick={start}>
              {pending === 'starting' ? 'Запускаем…' : 'Запустить пример'}
            </button>
            <button type="button" disabled={changed || pending !== null}
              onClick={() => navigate('/', { state: { scenario } })}>Настроить нагрузку</button>
          </div>
        </section>
      </div>
    </>
  )
}

function demoScenario({ capacity_rps, settings, target_url }: EmulatorConfiguration): Scenario {
  // Пять ступеней вокруг расчётной ёмкости пула, до 120 запросов/с для быстрого примера.
  const capacity = Math.min(80, Math.max(0.004, capacity_rps || 40))
  const increment = Number((capacity / 4).toFixed(3))
  return {
    name: `Эмулятор · ${modes.find((mode) => mode.value === settings.mode)?.name ?? settings.mode}`,
    target: { url: target_url, method: 'GET' },
    steps: [2, 3, 4, 5, 6].map((factor) => ({
      target_rps: Number((increment * factor).toFixed(3)),
      duration_seconds: 10,
    })),
  }
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : 'Не удалось выполнить запрос'
}
