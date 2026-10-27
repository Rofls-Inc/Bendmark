import type { Analysis, StepResult } from '../api/types'
import { P99_THRESHOLD_MS, firstSkippedStep, formatNumber, hasVerdicts, stepVerdict, type StepVerdict } from '../run/view'

interface Props {
  steps: StepResult[]
  analysis?: Analysis | null
  // Ступень, которая идёт сейчас: строка-заглушка под таблицей
  runningStep?: { index: number; targetRps: number } | null
}

const verdictLabel: Record<StepVerdict, string> = {
  ok: 'норма',
  limit: 'предел',
  failed: 'отказ',
  after: '',
  'not-analyzed': 'не анализ.',
}

// Почему ступень не дошла до анализатора
function notAnalyzedReason(index: number, skippedFrom: number | null): string {
  return skippedFrom !== null && index >= skippedFrom
    ? 'Агент не успевал отправлять запросы — замер неточный, анализатор его не берёт'
    : 'Ступень не попала в анализ: прогон остановлен'
}

export function StepsTable({ steps, analysis = null, runningStep }: Props) {
  const sorted = [...steps].sort((a, b) => a.index - b.index)
  const withVerdicts = hasVerdicts(analysis)
  // Колонка пропусков нужна, только если агент хоть раз не успевал
  const showSkipped = sorted.some((s) => s.skipped_count > 0)
  const skippedFrom = firstSkippedStep(sorted)
  const columns = 7 + (showSkipped ? 1 : 0) + (withVerdicts ? 1 : 0)

  return (
    // На узком экране таблица прокручивается сама, а не растягивает страницу
    <div className="table-scroll">
      <table className="steps">
        <thead>
          <tr>
            <th>Ступ.</th>
            <th>Нагрузка</th>
            <th>Пропускная</th>
            <th>p50</th>
            <th>p90</th>
            <th>p99</th>
            <th>Ошибки</th>
            {showSkipped && <th>Пропуски</th>}
            {withVerdicts && <th>Статус</th>}
          </tr>
        </thead>
        <tbody>
          {sorted.map((s) => {
            const verdict = stepVerdict(s, analysis)
            return (
              <tr key={s.index} className={verdict ? `verdict-${verdict}` : undefined}>
                <td>{s.index}</td>
                <td>{formatNumber(s.target_rps)}</td>
                <td>{formatNumber(s.throughput_rps, 1)}</td>
                <td>{formatNumber(s.latency_ms.p50)}</td>
                <td>{formatNumber(s.latency_ms.p90)}</td>
                <td className={s.latency_ms.p99 > P99_THRESHOLD_MS ? 'bad' : undefined}>
                  {formatNumber(s.latency_ms.p99)}
                </td>
                <td>
                  {formatNumber(s.errors.rate_percent, 1)} %
                  <span className="muted"> · {formatNumber(s.errors.count)}</span>
                </td>
                {showSkipped && (
                  <td
                    className={s.skipped_count > 0 ? 'warn' : 'muted'}
                    title={s.skipped_count > 0 ? 'Агент не успевал отправлять запросы' : undefined}
                  >
                    {formatNumber(s.skipped_count)}
                  </td>
                )}
                {withVerdicts && (
                  <td className="verdict" title={verdict === 'not-analyzed' ? notAnalyzedReason(s.index, skippedFrom) : undefined}>
                    {verdict && verdictLabel[verdict]}
                  </td>
                )}
              </tr>
            )
          })}
          {runningStep && (
            <tr className="running">
              <td>{runningStep.index}</td>
              <td>{formatNumber(runningStep.targetRps)}</td>
              <td colSpan={columns - 2} className="muted">идёт…</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}
