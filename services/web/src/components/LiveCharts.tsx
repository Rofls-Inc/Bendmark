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
import { axisProps, gridProps, legendProps, thresholdLabel, tooltipProps } from './chartStyle'
import { P99_THRESHOLD_MS, latencyAxis, rpsAxis, toChartPoints, toLatencyPoints } from '../run/view'

interface Props {
  steps: StepResult[]
  // Нагрузка всех ступеней сценария: ось сразу на весь прогон, будущие ступени пустые
  plannedTargets: number[]
}

const tooltipLabel = (value: unknown) => `Нагрузка ${value} запр/с`

function xAxis(plannedTargets: number[]) {
  return (
    <XAxis
      dataKey="target"
      type="number"
      domain={[Math.min(...plannedTargets), Math.max(...plannedTargets)]}
      ticks={plannedTargets}
      {...axisProps}
    />
  )
}

// Задержка по ступеням: p50, p90, p99 на логарифмической оси и порог отказа
export function LatencyChart({ steps, plannedTargets }: Props) {
  const points = toLatencyPoints(steps)
  const axis = latencyAxis(points)

  return (
    <ResponsiveContainer width="100%" height={260}>
      <LineChart data={points} margin={{ top: 16, right: 16, bottom: 8, left: 0 }}>
        <CartesianGrid {...gridProps} />
        {xAxis(plannedTargets)}
        <YAxis scale="log" domain={axis.domain} ticks={axis.ticks} {...axisProps} width={48} />
        <Tooltip {...tooltipProps} labelFormatter={tooltipLabel} />
        <Legend {...legendProps} />
        <ReferenceLine
          y={P99_THRESHOLD_MS}
          stroke="var(--bad)"
          strokeDasharray="4 4"
          label={{ value: `порог ${P99_THRESHOLD_MS} мс`, ...thresholdLabel }}
        />
        <Line dataKey="p50" name="p50, мс" stroke="var(--series-p50)" strokeWidth={1.5} isAnimationActive={false} />
        <Line dataKey="p90" name="p90, мс" stroke="var(--series-p90)" strokeWidth={1.5} isAnimationActive={false} />
        <Line dataKey="p99" name="p99, мс" stroke="var(--series-p99)" strokeWidth={2} isAnimationActive={false} />
      </LineChart>
    </ResponsiveContainer>
  )
}

// Пропускная против целевой нагрузки: пока линии совпадают, сервис справляется
export function ThroughputChart({ steps, plannedTargets }: Props) {
  const points = toChartPoints(steps)
  const axis = rpsAxis(plannedTargets.map((target) => ({ target, throughput: target, p99: null })))
  const planned = plannedTargets.map((target) => ({ target }))

  return (
    <ResponsiveContainer width="100%" height={260}>
      <LineChart margin={{ top: 16, right: 16, bottom: 8, left: 0 }}>
        <CartesianGrid {...gridProps} />
        {xAxis(plannedTargets)}
        <YAxis domain={axis.domain} ticks={axis.ticks} {...axisProps} width={48} />
        <Tooltip {...tooltipProps} labelFormatter={tooltipLabel} />
        <Legend {...legendProps} />
        <Line
          data={planned}
          dataKey="target"
          name="целевая нагрузка"
          stroke="var(--series-target)"
          strokeDasharray="3 3"
          dot={false}
          isAnimationActive={false}
        />
        <Line
          data={points}
          dataKey="throughput"
          name="пропускная, запр/с"
          stroke="var(--series-throughput)"
          strokeWidth={2}
          isAnimationActive={false}
        />
      </LineChart>
    </ResponsiveContainer>
  )
}
