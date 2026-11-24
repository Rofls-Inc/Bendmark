import type { MlRunDiagnosis } from '../api/types'
import { formatNumber } from './view'

const labels: Record<MlRunDiagnosis['label'], string> = {
  healthy: 'Сервис справился с нагрузкой',
  reject: 'Отказы при перегрузке',
  saturation: 'Насыщение',
  slow: 'Замедление',
}

export function MlDiagnosisPanel({ diagnosis }: { diagnosis: MlRunDiagnosis }) {
  const { peak_p99_ms, error_fraction, peak_queue_length, rejected_fraction } = diagnosis.measurements

  return (
    <section className="panel" aria-labelledby="ml-diagnosis-heading">
      <h2 id="ml-diagnosis-heading">Экспериментальная оценка ML</h2>
      <p><strong>{labels[diagnosis.label]}</strong></p>
      <dl className="facts">
        <dt>Максимальная p99</dt>
        <dd>{formatNumber(peak_p99_ms, 1)} мс</dd>
        <dt>Доля ошибок</dt>
        <dd>{formatNumber(error_fraction * 100, 2)} %</dd>
        <dt>Максимум в очереди</dt>
        <dd>{formatNumber(peak_queue_length)}</dd>
        <dt>Доля отказов по телеметрии</dt>
        <dd>{formatNumber(rejected_fraction * 100, 2)} %</dd>
      </dl>
    </section>
  )
}
