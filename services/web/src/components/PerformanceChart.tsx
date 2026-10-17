import {
  CartesianGrid,
  ComposedChart,
  Legend,
  Line,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import type { StepResult } from '../api/types'
import { axisProps, gridProps, legendProps, thresholdLabel, tooltipProps } from './chartStyle'
import { P99_THRESHOLD_MS, p99Axis, rpsAxis, toChartPoints } from '../run/view'

interface Props {
  steps: StepResult[]
  limitRps?: number | null
}

// Кривая производительности: пропускная слева, p99 справа на логарифмической оси
export function PerformanceChart({ steps, limitRps }: Props) {
  const points = toChartPoints(steps)
  const rps = rpsAxis(points)
  const p99 = p99Axis(points)

  return (
    <ResponsiveContainer width="100%" height={320}>
      <ComposedChart data={points} margin={{ top: 16, right: 8, bottom: 8, left: 0 }}>
        <CartesianGrid {...gridProps} />
        <XAxis
          dataKey="target"
          type="number"
          domain={['dataMin', 'dataMax']}
          ticks={points.map((p) => p.target)}
          {...axisProps}
        />
        <YAxis
          yAxisId="rps"
          domain={rps.domain}
          ticks={rps.ticks}
          {...axisProps}
          width={56}
          label={{ value: 'запр/с', angle: -90, position: 'insideLeft', fill: 'var(--text-3)', fontSize: 11 }}
        />
        <YAxis
          yAxisId="p99"
          orientation="right"
          scale="log"
          domain={p99.domain}
          ticks={p99.ticks}
          {...axisProps}
          width={56}
          label={{ value: 'p99, мс (лог)', angle: 90, position: 'insideRight', fill: 'var(--text-3)', fontSize: 11 }}
        />
        <Tooltip
          {...tooltipProps}
          labelFormatter={(value) => `Нагрузка ${value} запр/с`}
        />
        <Legend {...legendProps} />
        <ReferenceLine
          yAxisId="p99"
          y={P99_THRESHOLD_MS}
          stroke="var(--bad)"
          strokeDasharray="4 4"
          label={{ value: `порог ${P99_THRESHOLD_MS} мс`, ...thresholdLabel }}
        />
        {limitRps != null && (
          <ReferenceLine
            yAxisId="rps"
            x={limitRps}
            stroke="var(--accent)"
            label={{ value: 'предел', fill: 'var(--accent)', fontSize: 11, position: 'insideBottomRight' }}
          />
        )}
        <Line
          yAxisId="rps"
          dataKey="throughput"
          name="пропускная, запр/с"
          stroke="var(--series-throughput)"
          strokeWidth={2}
          isAnimationActive={false}
        />
        <Line
          yAxisId="rps"
          dataKey="target"
          name="целевая нагрузка"
          stroke="var(--series-target)"
          strokeDasharray="3 3"
          dot={false}
          isAnimationActive={false}
        />
        <Line
          yAxisId="p99"
          dataKey="p99"
          name="p99, мс"
          stroke="var(--series-p99)"
          strokeWidth={2}
          connectNulls={false}
          isAnimationActive={false}
        />
      </ComposedChart>
    </ResponsiveContainer>
  )
}
