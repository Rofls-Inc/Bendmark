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
        <CartesianGrid stroke="var(--grid)" vertical={false} />
        <XAxis
          dataKey="target"
          type="number"
          domain={['dataMin', 'dataMax']}
          ticks={points.map((p) => p.target)}
          stroke="var(--muted)"
          tickLine={false}
        />
        <YAxis
          yAxisId="rps"
          domain={rps.domain}
          ticks={rps.ticks}
          stroke="var(--muted)"
          tickLine={false}
          width={48}
        />
        <YAxis
          yAxisId="p99"
          orientation="right"
          scale="log"
          domain={p99.domain}
          ticks={p99.ticks}
          stroke="var(--muted)"
          tickLine={false}
          width={48}
        />
        <Tooltip
          contentStyle={{ background: 'var(--panel)', border: '1px solid var(--border)' }}
          labelFormatter={(value) => `Нагрузка ${value} запр/с`}
        />
        <Legend />
        <ReferenceLine
          yAxisId="p99"
          y={P99_THRESHOLD_MS}
          stroke="var(--red)"
          strokeDasharray="4 4"
          label={{ value: `порог ${P99_THRESHOLD_MS} мс`, fill: 'var(--red)', position: 'insideTopLeft' }}
        />
        {limitRps != null && (
          <ReferenceLine
            yAxisId="rps"
            x={limitRps}
            stroke="var(--fg)"
            label={{ value: 'предел', fill: 'var(--fg)', position: 'insideBottomRight' }}
          />
        )}
        <Line
          yAxisId="rps"
          dataKey="throughput"
          name="пропускная, запр/с"
          stroke="var(--green)"
          strokeWidth={2}
          isAnimationActive={false}
        />
        <Line
          yAxisId="rps"
          dataKey="target"
          name="целевая нагрузка"
          stroke="var(--muted)"
          strokeDasharray="3 3"
          dot={false}
          isAnimationActive={false}
        />
        <Line
          yAxisId="p99"
          dataKey="p99"
          name="p99, мс"
          stroke="var(--pink)"
          strokeWidth={2}
          connectNulls={false}
          isAnimationActive={false}
        />
      </ComposedChart>
    </ResponsiveContainer>
  )
}
