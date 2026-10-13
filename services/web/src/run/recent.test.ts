// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import { RECENT_KEY, RECENT_LIMIT, forgetRun, loadRecent, rememberRun } from './recent'

const run = (id: string, status: 'created' | 'running' | 'completed' = 'created') =>
  ({ id, status, name: `сценарий ${id}`, created_at: '2026-10-09T12:00:00Z' })

describe('недавние прогоны', () => {
  it('новый прогон в начале списка', () => {
    rememberRun(run('a'))
    rememberRun(run('b'))
    expect(loadRecent().map((r) => r.id)).toEqual(['b', 'a'])
  })

  it('статус обновляется без перестановки', () => {
    rememberRun(run('a'))
    rememberRun(run('b'))
    rememberRun(run('a', 'completed'))
    expect(loadRecent().map((r) => [r.id, r.status])).toEqual([['b', 'created'], ['a', 'completed']])
  })

  it(`хранит не больше ${RECENT_LIMIT}`, () => {
    for (let i = 0; i < RECENT_LIMIT + 3; i++)
      rememberRun(run(String(i)))
    const ids = loadRecent().map((r) => r.id)
    expect(ids).toHaveLength(RECENT_LIMIT)
    expect(ids[0]).toBe(String(RECENT_LIMIT + 2))
  })

  it('забыть прогон', () => {
    rememberRun(run('a'))
    forgetRun('a')
    expect(loadRecent()).toEqual([])
  })

  it('битые данные в хранилище не ломают список', () => {
    localStorage.setItem(RECENT_KEY, '{не json')
    expect(loadRecent()).toEqual([])
    localStorage.setItem(RECENT_KEY, JSON.stringify([{ id: 1 }, run('ok')]))
    expect(loadRecent().map((r) => r.id)).toEqual(['ok'])
  })

  it('недоступное хранилище: пустой список и без исключений', () => {
    const get = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('denied') })
    const set = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('denied') })
    try {
      expect(() => rememberRun(run('a'))).not.toThrow()
      expect(loadRecent()).toEqual([])
    } finally {
      get.mockRestore()
      set.mockRestore()
    }
  })
})
