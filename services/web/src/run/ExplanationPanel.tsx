import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { explainRun } from '../api/client'
import type { RunExplanation } from '../api/types'
import { formatNumber } from './view'

type State =
  | { kind: 'idle' | 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; explanation: RunExplanation }

const metricLabels: Record<RunExplanation['facts'][number]['metric'], { label: string; unit: string }> = {
  request_count: { label: 'Завершённых попыток', unit: '' },
  skipped_count: { label: 'Пропусков агента', unit: '' },
  throughput_rps: { label: 'Пропускная способность', unit: ' запр/с' },
  p99_ms: { label: 'Задержка p99', unit: ' мс' },
  error_rate_percent: { label: 'Доля ошибок', unit: ' %' },
}

export function ExplanationPanel({ runId }: { runId: string }) {
  const [state, setState] = useState<State>({ kind: 'idle' })
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])

  async function generate() {
    if (pending.current)
      return

    const controller = new AbortController()
    pending.current = controller
    setState({ kind: 'loading' })
    try {
      const explanation = await explainRun(runId, controller.signal)
      if (!controller.signal.aborted)
        setState({ kind: 'ready', explanation })
    } catch (error) {
      if (!controller.signal.aborted)
        setState({
          kind: 'error',
          message: error instanceof Error ? error.message : 'Не удалось получить GPT-объяснение.',
        })
    } finally {
      pending.current = null
    }
  }

  return (
    <section className="panel explanation" aria-label="GPT-объяснение">
      <h2>GPT-объяснение</h2>
      {state.kind === 'ready' ? (
        <div className="explanation-details" aria-live="polite">
          <h3>Гипотеза</h3>
          <p>{state.explanation.hypothesis}</p>
          <h3>Факты</h3>
          <ul className="reasons">
            {state.explanation.facts.map((fact) => {
              const { label, unit } = metricLabels[fact.metric]
              return (
                <li key={`${fact.step_index}-${fact.metric}`}>
                  Ступень {fact.step_index} при {formatNumber(fact.target_rps, 3)} запр/с.
                  {' '}{label} <strong>{formatNumber(fact.value, 3)}{unit}</strong>
                </li>
              )
            })}
          </ul>
          <h3>Проверочный эксперимент</h3>
          <p>{state.explanation.experiment}</p>
          {!!state.explanation.sources?.length && (
            <>
              <h3 id="similar-reports-heading">Похожие разборы</h3>
              <ul className="reasons" aria-labelledby="similar-reports-heading">
                {state.explanation.sources.map((source) => (
                  <li key={source.run_id}>
                    <Link to={`/runs/${encodeURIComponent(source.run_id)}`}>
                      {source.scenario_name || `Прогон ${source.run_id.slice(0, 8)}`}
                    </Link>
                    {source.finished_at && (
                      <> · <time dateTime={source.finished_at}>{new Date(source.finished_at).toLocaleString('ru-RU')}</time></>
                    )}
                    <p>{source.summary}</p>
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      ) : (
        <>
          {state.kind === 'idle' &&
            <p className="placeholder">Передаёт OpenAI замеры, вывод анализатора и найденные похожие разборы.</p>}
          {state.kind === 'error' && <p className="alert" role="alert">{state.message}</p>}
          <button type="button" onClick={generate} disabled={state.kind === 'loading'}>
            {state.kind === 'loading' ? 'Готовим объяснение…'
              : state.kind === 'error' ? 'Повторить запрос к GPT' : 'Объяснить через GPT'}
          </button>
        </>
      )}
    </section>
  )
}
