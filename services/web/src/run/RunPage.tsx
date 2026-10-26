import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { resultUrl } from '../api/client'
import { FINAL_STATUSES, type Run } from '../api/types'
import { LatencyChart, ThroughputChart } from '../components/LiveCharts'
import { PerformanceChart } from '../components/PerformanceChart'
import { State } from '../components/State'
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
    return <div className="page"><State kind="loading" title="Загружаем прогон…" /></div>
  if (state.kind === 'error')
    return (
      <div className="page">
        <State kind="error" title={state.message}>
          <Link to="/">К новому прогону</Link>
        </State>
      </div>
    )

  const { run, offline } = state
  return FINAL_STATUSES.includes(run.status)
    ? <Report run={run} offline={offline} />
    : <Live run={run} offline={offline} onStopped={applyRun} />
}

function Header({ run, subtitle, actions }: { run: Run; subtitle: string; actions?: ReactNode }) {
  const planned = run.scenario.steps.reduce((sum, s) => sum + s.duration_seconds, 0)
  return (
    <header className="page-header">
      <div>
        <p className="eyebrow">{subtitle}</p>
        <h1>{run.scenario.name}</h1>
        <div className="meta">
          <StatusBadge status={run.status} />
          <span>{run.scenario.target.method} <code>{run.scenario.target.url}</code></span>
          <span>{run.scenario.steps.length} ступеней · {formatDuration(planned)}</span>
        </div>
      </div>
      {actions && <div className="actions">{actions}</div>}
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

function Live({ run, offline, onStopped }: PageProps & { onStopped: (run: Run) => void }) {
  const steps = run.steps
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
      {offline && <Offline />}

      {run.status === 'created' && (
        <State kind="loading" title="Подготовка">
          <p className="muted">Прогон в очереди и ждёт агента. Координатор запускает прогоны по одному.</p>
        </State>
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
        className={`progress${run.status === 'created' ? ' idle' : ''}`}
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
        <div className="grid even">
          <section className="panel">
            <div className="panel-head">
              <h2>Задержка</h2>
              <span className="hint">мс, логарифмическая ось</span>
            </div>
            <LatencyChart steps={steps} plannedTargets={planned} />
          </section>
          <section className="panel">
            <div className="panel-head">
              <h2>Пропускная</h2>
              <span className="hint">запр/с против целевой</span>
            </div>
            <ThroughputChart steps={steps} plannedTargets={planned} />
          </section>
        </div>
      )}

      <section className="panel">
        <div className="panel-head">
          <h2>Ступени</h2>
          <span className="hint">{steps.length} из {total}</span>
        </div>
        {steps.length === 0 && !current ? (
          <State kind="empty" title="Ступеней пока нет">
            <p className="muted">Результаты придут после первой ступени.</p>
          </State>
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
  const navigate = useNavigate()
  const { steps, analysis } = run
  const failed = analysis?.failed_step_index != null
    ? steps.find((s) => s.index === analysis.failed_step_index)
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
      {offline && <Offline />}

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

      <LimitCard run={run} failed={failed} />

      {skipped !== null && <SkippedNote step={skipped} />}

      {steps.length === 0 ? (
        <State kind="empty" title="Ни одна ступень не завершилась">
          <p className="muted">Кривой и таблицы нет: агент не прислал ни одной полной ступени.</p>
        </State>
      ) : (
        <div className="grid report">
          <div className="column">
            <section className="panel">
              <div className="panel-head">
                <h2>Кривая производительности</h2>
                <span className="hint">пропускная слева · p99 справа, лог</span>
              </div>
              <PerformanceChart
                steps={steps}
                limitRps={analysis?.status === 'found' ? analysis.limit_rps : null}
              />
            </section>
            <section className="panel">
              <div className="panel-head">
                <h2>Ступени</h2>
                <span className="hint">{steps.length} из {run.scenario.steps.length}</span>
              </div>
              <StepsTable steps={steps} analysis={analysis} />
            </section>
          </div>
          <div className="column">
            <RunFacts run={run} />
            <section className="panel">
              <h2>Почему отказ</h2>
              <Reasons analysis={analysis} />
            </section>
            <section className="panel">
              <h2>Диагноз</h2>
              <DiagnosisNote analysis={analysis} />
            </section>
          </div>
        </div>
      )}
    </div>
  )
}

// Главный вывод отчёта: предел или почему его нет, и что было на ступени отказа
function LimitCard({ run, failed }: { run: Run; failed?: Run['steps'][number] }) {
  const head = headline(run)
  return (
    <section className={`limit kind-${head.kind}`} aria-label="Предел">
      <div className="limit-value">
        <span className="label">Предел</span>
        {head.value != null
          ? (
            <p className="big">
              {head.kind === 'lower-bound' && '≥ '}
              {formatNumber(head.value)}
              <span className="unit">запр/с</span>
            </p>
          )
          : <p className="caption">{head.kind === 'pending' ? 'Ищем предел…' : 'Не определён'}</p>}
      </div>
      <div>
        <p className="limit-text">{head.caption}</p>
        {failed && (
          <dl className="facts">
            <dt>Отказ на нагрузке</dt>
            <dd>{formatNumber(failed.target_rps)} запр/с</dd>
            <dt>p99 на отказе</dt>
            <dd>{formatNumber(failed.latency_ms.p99)} мс</dd>
            <dt>Ошибки на отказе</dt>
            <dd>{formatNumber(failed.errors.rate_percent, 1)} %</dd>
          </dl>
        )}
      </div>
    </section>
  )
}

function RunFacts({ run }: { run: Run }) {
  const peak = Math.max(...run.scenario.steps.map((s) => s.target_rps))
  const requests = run.steps.reduce((sum, s) => sum + s.request_count, 0)
  return (
    <section className="panel">
      <h2>Прогон</h2>
      <dl className="facts">
        <dt>Ступеней</dt>
        <dd>{run.steps.length} из {run.scenario.steps.length}</dd>
        <dt>Пиковая нагрузка</dt>
        <dd>{formatNumber(peak)} запр/с</dd>
        <dt>Запросов отправлено</dt>
        <dd>{formatNumber(requests)}</dd>
        <dt>Начат</dt>
        <dd>{formatTime(run.started_at)}</dd>
        <dt>Завершён</dt>
        <dd>{formatTime(run.finished_at)}</dd>
      </dl>
    </section>
  )
}

function formatTime(iso: string | null): string {
  if (!iso)
    return '—'
  return new Date(iso).toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })
}

function Reasons({ analysis }: { analysis: Run['analysis'] }) {
  if (!analysis)
    return <p className="placeholder">Анализ не выполнялся.</p>
  if (analysis.reasons.length > 0)
    return (
      <ul className="reasons">
        {analysis.reasons.map((r) => <li key={r}>{r}</li>)}
      </ul>
    )
  return <p className="placeholder">{analysis.status === 'not_found' ? 'Ни одна ступень не отказала.' : analysis.message}</p>
}

// Пока только текст анализатора; причины и уверенность по макету — отдельной задачей
function DiagnosisNote({ analysis }: { analysis: Run['analysis'] }) {
  const diagnosis = analysis?.diagnosis
  if (!diagnosis)
    return <p className="placeholder">Анализатор не поставил диагноз для этого прогона.</p>
  return (
    <>
      <p>{diagnosis.explanation}</p>
      {diagnosis.evidence.length > 0 && (
        <ul className="reasons">
          {diagnosis.evidence.map((e) => <li key={e}>{e}</li>)}
        </ul>
      )}
    </>
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
