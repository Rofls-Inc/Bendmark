import { Link, useParams } from 'react-router'
import { FINAL_STATUSES, type Run } from '../api/types'
import { PerformanceChart } from '../components/PerformanceChart'
import { StatusBadge } from '../components/StatusBadge'
import { StepsTable } from '../components/StepsTable'
import { useRun } from './useRun'
import { currentStep, formatDuration, formatNumber, headline } from './view'

export function RunPage() {
  const { id = '' } = useParams()
  const state = useRun(id)

  if (state.kind === 'loading')
    return <p className="page muted">Загружаем прогон…</p>
  if (state.kind === 'error')
    return (
      <div className="page">
        <p className="alert">{state.message}</p>
        <Link to="/">К сценарию</Link>
      </div>
    )

  const { run } = state
  return FINAL_STATUSES.includes(run.status) ? <Report run={run} /> : <Live run={run} />
}

function Header({ run, subtitle }: { run: Run; subtitle: string }) {
  const planned = run.scenario.steps.reduce((sum, s) => sum + s.duration_seconds, 0)
  return (
    <header className="page-header">
      <div>
        <h1>{run.scenario.name}</h1>
        <p className="muted">
          {subtitle} · <StatusBadge status={run.status} /> · {run.scenario.target.method}{' '}
          <code>{run.scenario.target.url}</code> · {formatDuration(planned)}
        </p>
      </div>
    </header>
  )
}

function Live({ run }: { run: Run }) {
  const steps = run.steps ?? []
  const current = currentStep(run)
  const total = run.scenario.steps.length
  const last = steps.at(-1)

  return (
    <div className="page">
      <Header run={run} subtitle="прогон" />

      <section className="stats">
        <Stat label="Ступень" value={current ? `${current} из ${total}` : `— из ${total}`} />
        <Stat label="Пропускная" value={last ? formatNumber(last.throughput_rps) : '—'} unit="запр/с" />
        <Stat label="p99" value={last ? formatNumber(last.latency_ms.p99) : '—'} unit="мс" />
        <Stat label="Ошибки" value={last ? formatNumber(last.errors.rate_percent, 1) : '—'} unit="%" />
      </section>

      <section className="panel">
        <h2>Ступени</h2>
        {steps.length === 0 && !current ? (
          <p className="muted">
            {run.status === 'created'
              ? 'Прогон создан и ждёт агента. Запуск через агента появится в #33.'
              : 'Результатов ступеней пока нет.'}
          </p>
        ) : (
          <StepsTable
            steps={steps}
            runningStep={current && current > steps.length
              ? { index: current, targetRps: run.scenario.steps[current - 1].target_rps }
              : null}
          />
        )}
      </section>
    </div>
  )
}

function Report({ run }: { run: Run }) {
  const steps = run.steps ?? []
  const head = headline(run)
  const failed = run.limit?.failed_step_index != null
    ? steps.find((s) => s.index === run.limit?.failed_step_index)
    : undefined

  return (
    <div className="page">
      <Header run={run} subtitle="отчёт" />

      <section className="headline">
        <div>
          <p className="big">
            {head.kind === 'lower-bound' && '≥ '}
            {head.value != null ? formatNumber(head.value) : '—'}
            {head.value != null && <span className="unit"> запр/с</span>}
          </p>
          <p className="muted">{head.caption}</p>
        </div>
        {failed && (
          <p className="muted">
            отказ на {formatNumber(failed.target_rps)} запр/с · p99 {formatNumber(failed.latency_ms.p99)} мс ·
            ошибки {formatNumber(failed.errors.rate_percent, 1)} %
          </p>
        )}
      </section>

      {steps.length === 0 ? (
        <section className="panel">
          <p className="muted">
            {run.status === 'failed' ? 'Прогон завершился сбоем до первой ступени.' : 'Результатов ступеней нет.'}
          </p>
        </section>
      ) : (
        <div className="grid-2 wide-left">
          <section className="panel">
            <h2>Кривая производительности</h2>
            <PerformanceChart steps={steps} limitRps={run.limit?.found ? run.limit.limit_rps : null} />
          </section>
          <section className="panel">
            <h2>Почему отказ</h2>
            {run.limit?.reasons.length ? (
              <ul className="reasons">
                {run.limit.reasons.map((r) => <li key={r}>{r}</li>)}
              </ul>
            ) : (
              <p className="muted">{run.limit ? 'Ни одна ступень не отказала.' : 'Анализ появится в #34.'}</p>
            )}
          </section>
        </div>
      )}

      {steps.length > 0 && (
        <section className="panel">
          <h2>Ступени</h2>
          <StepsTable steps={steps} limit={run.limit} />
        </section>
      )}
    </div>
  )
}

function Stat({ label, value, unit }: { label: string; value: string; unit?: string }) {
  return (
    <div className="stat">
      <span className="label">{label}</span>
      <span className="value">
        {value}
        {unit && <span className="unit"> {unit}</span>}
      </span>
    </div>
  )
}
