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

  const { run, offline } = state
  return FINAL_STATUSES.includes(run.status)
    ? <Report run={run} offline={offline} />
    : <Live run={run} offline={offline} />
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

interface PageProps {
  run: Run
  // Последний опрос не удался: показываем прошлый ответ и предупреждение
  offline: boolean
}

function Offline() {
  return <p className="alert">Нет связи с координатором, повторяем…</p>
}

function Live({ run, offline }: PageProps) {
  const steps = run.steps
  const current = currentStep(run)
  const total = run.scenario.steps.length
  const last = steps.at(-1)

  return (
    <div className="page">
      <Header run={run} subtitle="прогон" />
      {offline && <Offline />}

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
              ? 'Прогон в очереди: ждёт, пока освободится агент.'
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

function Report({ run, offline }: PageProps) {
  const { steps, analysis } = run
  const head = headline(run)
  const failed = analysis?.failed_step_index != null
    ? steps.find((s) => s.index === analysis.failed_step_index)
    : undefined

  return (
    <div className="page">
      <Header run={run} subtitle="отчёт" />
      {offline && <Offline />}

      {run.error && <p className="alert">{run.error}</p>}

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
          <p className="muted">Ни одна ступень не завершилась.</p>
        </section>
      ) : (
        <div className="grid-2 wide-left">
          <section className="panel">
            <h2>Кривая производительности</h2>
            <PerformanceChart
              steps={steps}
              limitRps={analysis?.status === 'found' ? analysis.limit_rps : null}
            />
            <p className="muted chart-hint">пропускная — левая ось · p99 — правая ось, логарифмическая</p>
          </section>
          <section className="panel">
            <h2>Почему отказ</h2>
            <Reasons analysis={analysis} />
          </section>
        </div>
      )}

      {steps.length > 0 && (
        <section className="panel">
          <h2>Ступени</h2>
          <StepsTable steps={steps} analysis={analysis} />
        </section>
      )}
    </div>
  )
}

function Reasons({ analysis }: { analysis: Run['analysis'] }) {
  if (!analysis)
    return <p className="muted">Анализ не выполнялся.</p>
  if (analysis.reasons.length > 0)
    return (
      <ul className="reasons">
        {analysis.reasons.map((r) => <li key={r}>{r}</li>)}
      </ul>
    )
  return <p className="muted">{analysis.status === 'not_found' ? 'Ни одна ступень не отказала.' : analysis.message}</p>
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
