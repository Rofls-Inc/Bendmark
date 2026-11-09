// Общий вид графиков: оси, сетка, подсказка и легенда на токенах из styles.css

export const axisProps = {
  stroke: 'var(--line-2)',
  tick: { fill: 'var(--text-3)', fontSize: 11, fontFamily: 'var(--font-num)' },
  tickLine: false,
} as const

export const gridProps = { stroke: 'var(--chart-grid)', vertical: false } as const

export const tooltipProps = {
  contentStyle: {
    background: 'var(--surface-3)',
    border: '1px solid var(--line-2)',
    borderRadius: 6,
    fontSize: 12,
  },
  labelStyle: { color: 'var(--text-1)' },
  itemStyle: { padding: 0 },
  cursor: { stroke: 'var(--line-3)' },
} as const

export const legendProps = {
  iconType: 'plainline',
  iconSize: 14,
  wrapperStyle: { fontSize: 12, color: 'var(--text-3)', paddingTop: 8 },
} as const

export const thresholdLabel = { fill: 'var(--bad)', fontSize: 11, position: 'insideTopLeft' } as const
