import { describe, expect, it } from 'vitest'
import { formatNumber, type Axis } from '../run/view'
import { anyAbove, shownFormatter, withShown } from './axisOverflow'

const axis: Axis = { domain: [0, 2500], ticks: [0, 500, 1000, 1500, 2000, 2500] }
const points = [
  { target: 1000, p50: 10, p99: 120 },
  { target: 2000, p50: 9000, p99: 40_000 },
  { target: 3000, p50: null, p99: null },
]

describe('withShown', () => {
  it('добавляет прижатые к оси копии серий и не трогает исходные значения', () => {
    expect(withShown(points, ['p50', 'p99'], axis)).toEqual([
      { target: 1000, p50: 10, p99: 120, p50Shown: 10, p99Shown: 120 },
      { target: 2000, p50: 9000, p99: 40_000, p50Shown: 2500, p99Shown: 2500 },
      { target: 3000, p50: null, p99: null, p50Shown: null, p99Shown: null },
    ])
  })
})

describe('anyAbove', () => {
  it('смотрит только на переданные серии', () => {
    expect(anyAbove(points, ['p99'], axis)).toBe(true)
    expect(anyAbove(points.slice(0, 1), ['p50', 'p99'], axis)).toBe(false)
    expect(anyAbove(points, [], axis)).toBe(false)
  })
})

describe('shownFormatter', () => {
  const format = shownFormatter(axis)!
  const call = (dataKey: string, value: number | undefined, payload: object) =>
    format(value, 'p99, мс', { dataKey, payload, graphicalItemId: dataKey }, 0, [])

  it('за потолком показывает настоящее значение с пометкой', () => {
    expect(call('p99Shown', 2500, points[1])).toEqual([`${formatNumber(40_000)} · выше шкалы`, 'p99, мс'])
  })

  it('в пределах оси — просто число', () => {
    expect(call('p99Shown', 120, points[0])).toEqual(['120', 'p99, мс'])
  })

  it('пустая ступень — прочерк', () => {
    expect(call('p99Shown', undefined, points[2])).toEqual(['—', 'p99, мс'])
  })

  it('серия без суффикса Shown берёт своё значение', () => {
    expect(call('throughput', 1100, { throughput: 1100 })).toEqual([formatNumber(1100), 'p99, мс'])
  })
})
