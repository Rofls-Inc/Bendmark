import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { resultUrl } from '../api/client'
import { FINAL_STATUSES, type Run } from '../api/types'
import { LatencyChart, ThroughputChart } from '../components/LiveCharts'
import { PerformanceChart } from '../components/PerformanceChart'
import { StatusBadge } from '../components/StatusBadge'
import { StepsTable } from '../components/StepsTable'
import { StopButton } from '../components/StopButton'
import { isDemoRun } from '../demo/demoRun'
import { useRun } from './useRun'
import {
  currentStep,
  firstSkippedStep,
  formatClock,
  formatDuration,
  formatNumber,
  headline,
  progress,
} from './view'

export function RunPage() {
  const { id = '' } = useParams()
  const { state, applyRun } = useRun(id)

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
  return FINAL_STATUSES.includes(run.status)
    ? <Report run={run} />
    : <Live run={run} onStopped={applyRun} />
}

function Header({ run, subtitle, actions }: { run: Run; subtitle: string; actions?: ReactNode }) {
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
      {actions && <div className="actions">{actions}</div>}
    </header>
  )
}

// Текущее время, обновляется раз в секунду, пока active
function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!active)
      return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [active])
  return now
}

function Live({ run, onStopped }: { run: Run; onStopped: (run: Run) => void }) {
  const steps = run.steps ?? []
  const now = useNow(run.status === 'running')
  const time = progress(run, now)
  // Ступень, которая идёт по часам; пока её результата нет, в таблице строка «идёт…»
  const current = time.step ?? currentStep(run)
  const total = run.scenario.steps.length
  const last = steps.at(-1)
  const planned = run.scenario.steps.map((s) => s.target_rps)
  const skipped = firstSkippedStep(steps)

  return (
    <div className="page">
      <Header
        run={run}
        subtitle="прогон"
        actions={<StopButton runId={run.id} onStopped={onStopped} disabled={isDemoRun(run.id)} />}
      />

      {run.status === 'created' && (
        <p className="note">Прогон в очереди и ждёт агента. Координатор запускает прогоны по одному.</p>
      )}

      <section className="stats">
        <Stat
          label="Ступень"
          value={time.step ? `${time.step} из ${total}` : `— из ${total}`}
          hint={time.step ? `${formatNumber(run.scenario.steps[time.step - 1].target_rps)} запр/с` : undefined}
        />
        <Stat label="Время" value={`${formatClock(time.elapsedSeconds)} / ${formatClock(time.totalSeconds)}`} />
        <Stat label="Пропускная" value={last ? formatNumber(last.throughput_rps) : '—'} unit="запр/с" hint="последняя ступень" />
        <Stat label="p99" value={last ? formatNumber(last.latency_ms.p99) : '—'} unit="мс" hint="последняя ступень" />
        <Stat label="Ошибки" value={last ? formatNumber(last.errors.rate_percent, 1) : '—'} unit="%" hint="последняя ступень" />
      </section>
      <div
        className="progress"
        role="progressbar"
        aria-label="Ход прогона"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(time.fraction * 100)}
      >
        <div style={{ width: `${time.fraction * 100}%` }} />
      </div>

      {skipped !== null && <SkippedNote step={skipped} />}

      {steps.length > 0 && (
        <div className="grid-2 even">
          <section className="panel">
            <h2>Задержка</h2>
            <LatencyChart steps={steps} plannedTargets={planned} />
          </section>
          <section className="panel">
            <h2>Пропускная</h2>
            <ThroughputChart steps={steps} plannedTargets={planned} />
          </section>
        </div>
      )}

      <section className="panel">
        <h2>Ступени</h2>
        {steps.length === 0 && !current ? (
          <p className="muted">Результаты придут после первой ступени.</p>
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
  const navigate = useNavigate()
  const steps = run.steps ?? []
  const head = headline(run)
  const failed = run.limit?.failed_step_index != null
    ? steps.find((s) => s.index === run.limit?.failed_step_index)
    : undefined
  const skipped = firstSkippedStep(steps)
  const downloadable = !isDemoRun(run.id) && (run.status === 'completed' || run.status === 'aborted')

  const actions = (
    <>
      <button type="button" onClick={() => navigate('/', { state: { scenario: run.scenario } })}>Повторить</button>
      {downloadable && (
        <a className="button" href={resultUrl(run.id)} download={`${run.scenario.name}-${run.id.slice(0, 8)}.json`}>
          Скачать result.json
        </a>
      )}
    </>
  )

  return (
    <div className="page">
      <Header run={run} subtitle="отчёт" actions={actions} />

      {run.status === 'failed' && (
        <p className="alert">
          {run.error || 'Прогон завершился сбоем.'}
          {steps.length > 0 && ` Полученные ступени (${steps.length} из ${run.scenario.steps.length}) сохранены для диагностики.`}
        </p>
      )}
      {run.status === 'aborted' && (
        <p className="note">
          {run.error ? `${run.error}. ` : 'Прогон остановлен. '}
          Данные есть только по полностью пройденным ступеням: {steps.length} из {run.scenario.steps.length}.
        </p>
      )}

      <section className="headline">
        <div>
          {head.value != null && (
            <p className="big">
              {head.kind === 'lower-bound' && '≥ '}
              {formatNumber(head.value)}
              <span className="unit"> запр/с</span>
            </p>
          )}
          <p className={head.value != null ? 'muted' : 'caption'}>{head.caption}</p>
        </div>
        {failed && (
          <p className="muted">
            отказ на {formatNumber(failed.target_rps)} запр/с · p99 {formatNumber(failed.latency_ms.p99)} мс ·
            ошибки {formatNumber(failed.errors.rate_percent, 1)} %
          </p>
        )}
      </section>

      {skipped !== null && <SkippedNote step={skipped} />}

      {steps.length === 0 ? (
        <section className="panel">
          <p className="muted">
            {run.status === 'failed' ? 'Ни одна ступень не завершилась.' : 'Результатов ступеней нет.'}
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
              <p className="muted">
                {run.limit ? 'Ни одна ступень не отказала.' : 'Анализ появится, когда координатор начнёт вызывать анализатор (#34).'}
              </p>
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

function SkippedNote({ step }: { step: number }) {
  return (
    <p className="note warn">
      С {step}-й ступени агент не успевал отправлять запросы: все рабочие потоки были заняты.
      Замер этих ступеней неточный, предел ищется только по ступеням до неё.
    </p>
  )
}

function Stat({ label, value, unit, hint }: { label: string; value: string; unit?: string; hint?: string }) {
  return (
    <div className="stat">
      <span className="label">{label}</span>
      <span className="value">
        {value}
        {unit && <span className="unit"> {unit}</span>}
      </span>
      {hint && <span className="hint">{hint}</span>}
    </div>
  )
}
