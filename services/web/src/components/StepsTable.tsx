import type { Limit, StepResult } from '../api/types'
import { P99_THRESHOLD_MS, formatNumber, stepVerdict, type StepVerdict } from '../run/view'

interface Props {
  steps: StepResult[]
  limit?: Limit | null
  // Ступень, которая идёт сейчас: строка-заглушка под таблицей
  runningStep?: { index: number; targetRps: number } | null
}

const verdictLabel: Record<StepVerdict, string> = {
  ok: 'норма',
  limit: 'предел',
  failed: 'отказ',
  after: '',
}

export function StepsTable({ steps, limit, runningStep }: Props) {
  const sorted = [...steps].sort((a, b) => a.index - b.index)
  // Колонка пропусков нужна, только если агент хоть раз не успевал
  const showSkipped = sorted.some((s) => (s.skipped_count ?? 0) > 0)
  const columns = 7 + (showSkipped ? 1 : 0) + (limit ? 1 : 0)

  return (
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
            {limit && <th>Статус</th>}
          </tr>
        </thead>
        <tbody>
          {sorted.map((s) => {
            const verdict = stepVerdict(s, limit)
            const skipped = s.skipped_count ?? 0
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
                  <td className={skipped > 0 ? 'warn' : 'muted'} title={skipped > 0 ? 'Агент не успевал отправлять запросы' : undefined}>
                    {formatNumber(skipped)}
                  </td>
                )}
                {limit && <td className="verdict">{verdict && verdictLabel[verdict]}</td>}
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
