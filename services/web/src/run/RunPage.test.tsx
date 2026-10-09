// @vitest-environment jsdom
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Run, RunStatus } from '../api/types'
import { demoRun } from '../demo/demoRun'
import { json, mockFetch } from '../test/fetch'
import { RunPage } from './RunPage'
import { loadRecent } from './recent'
import { POLL_INTERVAL_MS } from './useRun'

const ID = '7f1c2b8e-0000-4000-8000-000000000001'

function run(status: RunStatus, stepCount: number, extra: Partial<Run> = {}): Run {
  return {
    ...demoRun,
    id: ID,
    status,
    started_at: status === 'created' ? null : new Date().toISOString(),
    finished_at: null,
    error: null,
    steps: demoRun.steps!.slice(0, stepCount),
    // Координатор пока не отдаёт предел (#34)
    limit: undefined,
    ...extra,
  }
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={[`/runs/${ID}`]}>
      <Routes>
        <Route path="/runs/:id" element={<RunPage />} />
        <Route path="/" element={<p>форма сценария</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

// Таймеры фейковые, но идут сами: так findBy* дожидается промисов fetch
beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

const user = () => userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
const tick = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms))

describe('RunPage', () => {
  it('опрашивает прогон, пока он не завершится', async () => {
    const answers = [run('running', 1), run('completed', 7)]
    const calls = mockFetch(() => json(200, answers.shift() ?? run('completed', 7)))
    renderPage()

    expect(await screen.findByText('идёт')).toBeTruthy()
    expect(screen.getByRole('progressbar')).toBeTruthy()
    await tick(POLL_INTERVAL_MS)
    expect(await screen.findByText('завершён')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Повторить' })).toBeTruthy()

    // После конечного статуса запросов больше нет
    await tick(POLL_INTERVAL_MS * 3)
    expect(calls.filter((c) => c.method === 'GET')).toHaveLength(2)
    expect(loadRecent()[0]).toMatchObject({ id: ID, status: 'completed' })
  })

  it('прогон в очереди ждёт агента', async () => {
    mockFetch(() => json(200, run('created', 0)))
    renderPage()
    expect(await screen.findByText(/в очереди и ждёт агента/)).toBeTruthy()
  })

  it('останавливает прогон после подтверждения', async () => {
    let stopped = false
    const aborted = run('aborted', 2, { finished_at: new Date().toISOString() })
    const calls = mockFetch((call) => {
      if (call.method === 'POST')
        stopped = true
      return json(200, stopped ? aborted : run('running', 2))
    })
    renderPage()
    const u = user()

    await u.click(await screen.findByRole('button', { name: 'Остановить' }))
    expect(screen.getByText('Остановить прогон?')).toBeTruthy()
    await u.click(screen.getByRole('button', { name: 'Остановить' }))

    expect(await screen.findByText('остановлен')).toBeTruthy()
    expect(screen.getByText(/только по полностью пройденным ступеням: 2 из 7/)).toBeTruthy()
    expect(calls.find((c) => c.method === 'POST')?.url).toBe(`/api/runs/${ID}/stop`)
    // Остановленный прогон больше не опрашивается
    const gets = calls.filter((c) => c.method === 'GET').length
    await tick(POLL_INTERVAL_MS * 2)
    expect(calls.filter((c) => c.method === 'GET')).toHaveLength(gets)
  })

  it('отмена подтверждения не останавливает', async () => {
    const calls = mockFetch(() => json(200, run('running', 1)))
    renderPage()
    const u = user()
    await u.click(await screen.findByRole('button', { name: 'Остановить' }))
    await u.click(screen.getByRole('button', { name: 'Отмена' }))
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
  })

  it('202: остановка идёт, опрос продолжается до итога', async () => {
    // После 202 первый опрос ещё видит running, итог приходит со следующим
    let pollsAfterStop = -1
    mockFetch((call) => {
      if (call.method === 'POST') {
        pollsAfterStop = 0
        return json(202, run('running', 2))
      }
      if (pollsAfterStop >= 0)
        pollsAfterStop++
      return json(200, pollsAfterStop > 1 ? run('aborted', 2) : run('running', 2))
    })
    renderPage()
    const u = user()
    await u.click(await screen.findByRole('button', { name: 'Остановить' }))
    await u.click(screen.getByRole('button', { name: 'Остановить' }))

    expect(await screen.findByText(/Агент снимает нагрузку/)).toBeTruthy()
    await tick(POLL_INTERVAL_MS)
    expect(await screen.findByText('остановлен')).toBeTruthy()
  })

  it('409: показывает причину от координатора', async () => {
    mockFetch((call) => call.method === 'POST'
      ? json(409, { detail: 'Прогон уже завершён со статусом completed, остановка не нужна' })
      : json(200, run('running', 6)))
    renderPage()
    const u = user()
    await u.click(await screen.findByRole('button', { name: 'Остановить' }))
    await u.click(screen.getByRole('button', { name: 'Остановить' }))
    expect((await screen.findByRole('alert')).textContent).toContain('остановка не нужна')
  })

  it('сбой: текст ошибки и полученные ступени', async () => {
    mockFetch(() => json(200, run('failed', 2, { error: 'Агент недоступен' })))
    renderPage()
    expect(await screen.findByText(/Агент недоступен/)).toBeTruthy()
    expect(screen.getByText(/сохранены для диагностики/)).toBeTruthy()
    // result.json есть только у completed и aborted
    expect(screen.queryByText('Скачать result.json')).toBeNull()
  })

  it('завершённый прогон можно скачать как result.json', async () => {
    mockFetch(() => json(200, run('completed', 7)))
    renderPage()
    const link = await screen.findByRole('link', { name: 'Скачать result.json' })
    expect(link.getAttribute('href')).toBe(`/api/runs/${ID}/result`)
  })

  it('пропуски агента отмечены', async () => {
    mockFetch(() => json(200, run('completed', 3, {
      steps: demoRun.steps!.slice(0, 3).map((s) => (s.index === 3 ? { ...s, skipped_count: 40 } : s)),
    })))
    renderPage()
    expect(await screen.findByText(/С 3-й ступени агент не успевал/)).toBeTruthy()
    expect(screen.getByRole('columnheader', { name: 'Пропуски' })).toBeTruthy()
  })

  it('404: прогон не найден, опрос не повторяется', async () => {
    const calls = mockFetch(() => json(404, {}))
    renderPage()
    expect(await screen.findByText('Прогон не найден')).toBeTruthy()
    await tick(POLL_INTERVAL_MS * 2)
    expect(calls).toHaveLength(1)
  })

  it('«Повторить» открывает форму', async () => {
    mockFetch(() => json(200, run('completed', 7)))
    renderPage()
    await user().click(await screen.findByRole('button', { name: 'Повторить' }))
    expect(screen.getByText('форма сценария')).toBeTruthy()
  })
})
