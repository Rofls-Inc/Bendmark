import {
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import type { StepResult } from '../api/types'
import { anyAbove, overflowDot, shownFormatter, withShown } from './axisOverflow'
import { axisProps, gridProps, legendProps, thresholdLabel, tooltipProps } from './chartStyle'
import { P99_THRESHOLD_MS, formatNumber, p99Axis, rpsAxis, toChartPoints, type ChartPoint } from '../run/view'

interface Props {
  steps: StepResult[]
  limitRps?: number | null
}

// syncId связывает подсказки: наведение на ступень показывает её на обоих графиках
const SYNC_ID = 'performance'
const margin = { top: 16, right: 16, bottom: 0, left: 0 }
const tooltipLabel = (value: unknown) => `Нагрузка ${value} запр/с`

// Кривая производительности — два графика с общей осью нагрузки, у каждого одна ось Y.
// Сверху пропускная против целевой (одни единицы, пока линии совпадают — сервис справляется),
// снизу p99 на линейной оси с порогом. Подпись оси — цветом её линии.
export function PerformanceChart({ steps, limitRps }: Props) {
  const points = toChartPoints(steps)
  const rps = rpsAxis(points)
  const p99 = p99Axis(points)
  const latency = withShown(points, ['p99'], p99)
  const above = anyAbove(points, ['p99'], p99)

  return (
    <div>
      <ResponsiveContainer width="100%" height={220}>
        <LineChart data={points} margin={margin} syncId={SYNC_ID}>
          <CartesianGrid {...gridProps} />
          {xAxis(points)}
          <YAxis
            domain={rps.domain}
            ticks={rps.ticks}
            {...axisProps}
            width={56}
            label={axisLabel('запр/с', 'var(--series-throughput)')}
          />
          <Tooltip {...tooltipProps} labelFormatter={tooltipLabel} />
          <Legend {...legendProps} />
          {limitLine(limitRps, true)}
          <Line
            dataKey="target"
            name="целевая нагрузка"
            stroke="var(--series-target)"
            strokeDasharray="3 3"
            dot={false}
            isAnimationActive={false}
          />
          <Line
            dataKey="throughput"
            name="пропускная, запр/с"
            stroke="var(--series-throughput)"
            strokeWidth={2}
            isAnimationActive={false}
          />
        </LineChart>
      </ResponsiveContainer>
      <ResponsiveContainer width="100%" height={200}>
        <LineChart data={latency} margin={margin} syncId={SYNC_ID}>
          <CartesianGrid {...gridProps} />
          {xAxis(points)}
          <YAxis
            domain={p99.domain}
            ticks={p99.ticks}
            {...axisProps}
            width={56}
            label={axisLabel('p99, мс', 'var(--series-p99)')}
          />
          <Tooltip {...tooltipProps} labelFormatter={tooltipLabel} formatter={shownFormatter(p99)} />
          <Legend {...legendProps} />
          <ReferenceLine
            y={P99_THRESHOLD_MS}
            stroke="var(--bad)"
            strokeDasharray="4 4"
            label={{ value: `порог ${P99_THRESHOLD_MS} мс`, ...thresholdLabel }}
          />
          {limitLine(limitRps)}
          <Line
            dataKey="p99Shown"
            name="p99, мс"
            stroke="var(--series-p99)"
            strokeWidth={2}
            dot={overflowDot('p99', p99, 'var(--series-p99)')}
            connectNulls={false}
            isAnimationActive={false}
          />
        </LineChart>
      </ResponsiveContainer>
      {above && (
        <p className="hint chart-note">
          p99 выше {formatNumber(p99.domain[1])} мс показан у верхнего края красной точкой,
          точные значения — в подсказке и таблице ступеней
        </p>
      )}
    </div>
  )
}

function xAxis(points: ChartPoint[]) {
  return (
    <XAxis
      dataKey="target"
      type="number"
      domain={['dataMin', 'dataMax']}
      ticks={points.map((p) => p.target)}
      {...axisProps}
    />
  )
}

function axisLabel(value: string, color: string) {
  return { value, angle: -90, position: 'insideLeft', fill: color, fontSize: 11 } as const
}

// Линия предела на обоих графиках, подпись — только на верхнем
function limitLine(limitRps: number | null | undefined, labeled = false) {
  if (limitRps == null)
    return null
  return (
    <ReferenceLine
      x={limitRps}
      stroke="var(--accent)"
      label={labeled ? { value: 'предел', fill: 'var(--accent)', fontSize: 11, position: 'insideBottomRight' } : undefined}
    />
  )
}
