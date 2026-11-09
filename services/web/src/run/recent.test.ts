// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { RunSummary } from '../api/types'
import { json, mockFetch } from '../test/fetch'
import { RECENT_LIMIT, loadRecent, refreshRecent } from './recent'

const summary = (id: string, status: RunSummary['status'] = 'completed'): RunSummary => ({
  id,
  scenario_name: `сценарий ${id}`,
  status,
  created_at: '2026-10-09T12:00:00Z',
  started_at: null,
  finished_at: null,
  analysis_status: null,
  limit_rps: null,
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('недавние прогоны', () => {
  it('до первого ответа - загрузка', () => {
    expect(loadRecent()).toEqual({ kind: 'loading' })
  })

  it(`первая страница GET /runs, ${RECENT_LIMIT} штук`, async () => {
    const calls = mockFetch(() => json(200, { runs: [summary('b', 'running'), summary('a')], next_before: 'a' }))
    await refreshRecent()
    expect(calls.map((c) => c.url)).toEqual([`/api/runs?limit=${RECENT_LIMIT}`])
    expect(loadRecent()).toEqual({ kind: 'ready', runs: [summary('b', 'running'), summary('a')] })
  })

  it('координатор недоступен: ошибка вместо списка', async () => {
    mockFetch(() => {
      throw new TypeError('Failed to fetch')
    })
    await refreshRecent()
    expect(loadRecent()).toEqual({ kind: 'error', message: 'Координатор недоступен' })
  })

  it('сбой после загрузки не стирает показанный список', async () => {
    mockFetch(() => json(200, { runs: [summary('a')], next_before: null }))
    await refreshRecent()
    mockFetch(() => json(502, {}))
    await refreshRecent()
    expect(loadRecent()).toEqual({ kind: 'ready', runs: [summary('a')] })
  })

  it('поздний ответ на старый запрос не затирает свежий', async () => {
    let releaseOld: (r: Response) => void = () => {}
    let first = true
    mockFetch(() => {
      if (first) {
        first = false
        return new Promise<Response>((resolve) => { releaseOld = resolve })
      }
      return json(200, { runs: [summary('new', 'running')], next_before: null })
    })
    const old = refreshRecent()
    await refreshRecent()
    releaseOld(json(200, { runs: [summary('old', 'created')], next_before: null }))
    await old
    expect(loadRecent()).toEqual({ kind: 'ready', runs: [summary('new', 'running')] })
  })
})
