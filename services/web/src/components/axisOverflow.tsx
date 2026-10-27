import type { DotItemDotProps, TooltipProps } from 'recharts'
import { clampToAxis, formatNumber, isAboveAxis, type Axis } from '../run/view'

// Задержка за потолком оси рисуется у верхнего края: серия берёт значение из `${key}Shown`,
// а точка и подсказка помнят настоящее значение из `key`

const SHOWN = 'Shown'

export function withShown<T extends object>(points: T[], keys: (keyof T & string)[], axis: Axis) {
  return points.map((p) => ({
    ...p,
    ...Object.fromEntries(keys.map((k) => [k + SHOWN, clampToAxis(p[k] as number | null, axis)])),
  }))
}

export function anyAbove<T extends object>(points: T[], keys: (keyof T & string)[], axis: Axis): boolean {
  return points.some((p) => keys.some((k) => isAboveAxis(p[k] as number | null, axis)))
}

// Точка серии; за краем оси — залитая цветом отказа
export function overflowDot(key: string, axis: Axis, color: string) {
  return function OverflowDot({ cx, cy, index, payload }: DotItemDotProps) {
    if (cx == null || cy == null)
      return <g key={index} />
    const over = isAboveAxis(payload?.[key] ?? null, axis)
    return (
      <circle
        key={index}
        cx={cx}
        cy={cy}
        r={over ? 4 : 3}
        fill={over ? 'var(--bad)' : 'var(--surface)'}
        stroke={over ? 'var(--bad)' : color}
        strokeWidth={2}
      />
    )
  }
}

// Подсказка показывает настоящее значение, а не прижатое к краю
export function shownFormatter(axis: Axis): TooltipProps['formatter'] {
  return (value, name, item) => {
    const key = String(item.dataKey ?? '')
    const real = (key.endsWith(SHOWN) ? item.payload?.[key.slice(0, -SHOWN.length)] : value) as number | null | undefined
    if (real == null)
      return ['—', name]
    return [isAboveAxis(real, axis) ? `${formatNumber(real)} · выше шкалы` : formatNumber(real), name]
  }
}
