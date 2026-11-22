// @vitest-environment jsdom
import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Run, RunStatus } from '../api/types'
import { demoRun } from '../demo/demoRun'
import { type Call, json, mockFetch } from '../test/fetch'
import { RunPage } from './RunPage'
import { refreshRecent } from './recent'
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
    steps: demoRun.steps.slice(0, stepCount),
    analysis: null,
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

// Опросы самого прогона, без запросов списка «Недавних»
const polls = (calls: Call[]) => calls.filter((c) => c.method === 'GET' && c.url === `/api/runs/${ID}`)
const listCalls = (calls: Call[]) => calls.filter((c) => c.url.startsWith('/api/runs?'))

const user = () => userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
const tick = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms))

describe('RunPage', () => {
  it('опрашивает прогон, пока он не завершится', async () => {
    const answers = [run('running', 1), run('completed', 7)]
    const calls = mockFetch((call) => call.url.startsWith('/api/runs?')
      ? json(200, { runs: [], next_before: null })
      : json(200, answers.shift() ?? run('completed', 7)))
    renderPage()

    expect(await screen.findByText('идёт')).toBeTruthy()
    expect(screen.getByRole('progressbar')).toBeTruthy()
    expect(listCalls(calls)).toHaveLength(0)
    await tick(POLL_INTERVAL_MS)
    expect(await screen.findByText('завершён')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Повторить' })).toBeTruthy()

    // После конечного статуса запросов больше нет
    await tick(POLL_INTERVAL_MS * 3)
    expect(polls(calls)).toHaveLength(2)
    // Прогон завершился: список «Недавних» перечитан с координатора
    expect(listCalls(calls)).toEqual([expect.objectContaining({ url: '/api/runs?limit=10' })])
  })

  it('устаревший статус в «Недавних» обновляется при открытии прогона', async () => {
    const calls = mockFetch((call) => call.url.startsWith('/api/runs?')
      ? json(200, { runs: [{ id: ID, scenario_name: 'abstock-home-ramp', status: 'running', created_at: '2026-10-09T12:00:00Z',
          started_at: null, finished_at: null, analysis_status: null, limit_rps: null }], next_before: null })
      : json(200, run('completed', 7)))
    // Панель загрузила список, пока прогон ещё шёл; потом он завершился без открытой страницы
    await refreshRecent()
    renderPage()

    expect(await screen.findByText('завершён')).toBeTruthy()
    expect(listCalls(calls)).toHaveLength(2)
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
    const gets = polls(calls).length
    await tick(POLL_INTERVAL_MS * 2)
    expect(polls(calls)).toHaveLength(gets)
    expect(listCalls(calls)).toHaveLength(1)
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

  it('показывает успешные ответы в окне и отличает ноль от пропущенного значения', async () => {
    mockFetch(() => json(200, run('completed', 3, {
      steps: [
        { ...demoRun.steps[0], successful_rps_in_step: 12.5 },
        { ...demoRun.steps[1], successful_rps_in_step: 0 },
        demoRun.steps[2],
      ],
    })))
    renderPage()

    expect(await screen.findByRole('columnheader', { name: 'Успехи в окне, RPS' })).toBeTruthy()
    const rows = within(screen.getByRole('table')).getAllByRole('row').slice(1)
    expect(rows.map((row) => within(row).getAllByRole('cell')[3].textContent))
      .toEqual(['12,5', '0', '—'])
  })

  it('пропуски агента отмечены', async () => {
    mockFetch(() => json(200, run('completed', 3, {
      steps: demoRun.steps.slice(0, 3).map((s) => (s.index === 3 ? { ...s, skipped_count: 40 } : s)),
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

  it('координатор недоступен при остановке: понятный текст вместо «Failed to fetch»', async () => {
    mockFetch((call) => {
      if (call.method === 'POST')
        throw new TypeError('Failed to fetch')
      return json(200, run('running', 1))
    })
    renderPage()
    const u = user()
    await u.click(await screen.findByRole('button', { name: 'Остановить' }))
    await u.click(screen.getByRole('button', { name: 'Остановить' }))
    expect(await screen.findByText(/Координатор недоступен/)).toBeTruthy()
  })

  it('«Повторить» открывает форму', async () => {
    mockFetch(() => json(200, run('completed', 7)))
    renderPage()
    await user().click(await screen.findByRole('button', { name: 'Повторить' }))
    expect(screen.getByText('форма сценария')).toBeTruthy()
  })

  it('отчёт начинается с карточки предела', async () => {
    mockFetch((call) => call.url.startsWith('/api/runs?')
      ? json(200, { runs: [], next_before: null })
      : json(200, { ...demoRun, id: ID }))
    renderPage()

    const card = await screen.findByRole('region', { name: 'Предел' })
    expect(card.textContent).toContain('350')
    expect(within(card).getByRole('heading', { name: 'Отказ на ступени 7' })).toBeTruthy()
    expect(card.textContent).toContain('400 запр/с')
    expect(card.textContent).toContain('812 мс')
    expect(card.textContent).toContain('2,4 %')
    expect(card.textContent).toContain('Предел не подтверждён')
    expect(screen.getByRole('heading', { name: 'Пропускная способность' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Задержка p99' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Диагноз' })).toBeTruthy()
    expect(screen.getByText('Анализатор не поставил диагноз для этого прогона.')).toBeTruthy()
  })

  it('диагноз показывает объяснение и доводы анализатора', async () => {
    const diagnosis = {
      status: 'diagnosed', cause: 'rejecting', confidence: 'high',
      explanation: 'Сервис отклоняет нагрузку',
      evidence: ['ступень 7: ошибок 2.4 %'],
    }
    mockFetch((call) => call.url.startsWith('/api/runs?')
      ? json(200, { runs: [], next_before: null })
      : json(200, { ...demoRun, id: ID, analysis: { ...demoRun.analysis, diagnosis } }))
    renderPage()

    expect(await screen.findByText('Сервис отклоняет нагрузку')).toBeTruthy()
    expect(screen.getByText('ступень 7: ошибок 2.4 %')).toBeTruthy()
  })

  it('готовый отчёт открывается без GPT и дополняется объяснением по запросу', async () => {
    let finish!: (response: Response) => void
    const model = new Promise<Response>((resolve) => { finish = resolve })
    const calls = mockFetch((call) => call.method === 'POST'
      ? model
      : json(200, { ...demoRun, id: ID }))
    renderPage()
    const button = await screen.findByRole('button', { name: 'Объяснить через GPT' })
    expect(calls.some((call) => call.method === 'POST')).toBe(false)
    await user().click(button)

    expect(calls.filter((call) => call.method === 'POST').map((call) => call.url))
      .toEqual([`/api/runs/${ID}/explanation`])
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Готовим объяснение…' }).disabled).toBe(true)
    expect(screen.getByRole('region', { name: 'Предел' }).textContent).toContain('350')
    expect(screen.getByRole('link', { name: 'Скачать result.json' })).toBeTruthy()

    await act(async () => finish(json(200, {
      hypothesis: 'На последней ступени запросы могли скапливаться в очереди.',
      facts: [{ step_index: 7, target_rps: 400, metric: 'p99_ms', value: 812 }],
      experiment: 'Повторите ступень со сбором длины очереди.',
    })))

    const panel = within(screen.getByRole('region', { name: 'GPT-объяснение' }))
    expect(panel.getByText('На последней ступени запросы могли скапливаться в очереди.')).toBeTruthy()
    expect(panel.getByRole('listitem').textContent).toContain('Ступень 7 при 400 запр/с. Задержка p99 812 мс')
    expect(panel.getByText('Повторите ступень со сбором длины очереди.')).toBeTruthy()
  })

  it('открывает исходный отчёт из списка похожих разборов', async () => {
    const sourceId = '7f1c2b8e-0000-4000-8000-000000000002'
    const calls = mockFetch((call) => call.method === 'POST'
      ? json(200, {
        hypothesis: 'Возможна очередь запросов.',
        facts: [{ step_index: 7, target_rps: 400, metric: 'p99_ms', value: 812 }],
        experiment: 'Измерьте длину очереди.',
        sources: [{
          run_id: sourceId, scenario_name: 'Рампа для очереди', finished_at: '2026-11-20T11:30:00Z',
          summary: 'Задержка выросла на двух ступенях.',
        }],
      })
      : call.url === `/api/runs/${sourceId}`
        ? json(200, { ...demoRun, id: sourceId, scenario: { ...demoRun.scenario, name: 'Исходный прогон' } })
        : json(200, { ...demoRun, id: ID }))
    renderPage()
    await user().click(await screen.findByRole('button', { name: 'Объяснить через GPT' }))

    const list = await screen.findByRole('list', { name: 'Похожие разборы' })
    expect(within(list).getByText('Задержка выросла на двух ступенях.')).toBeTruthy()
    const source = within(list).getByRole('link', { name: 'Рампа для очереди' })
    expect(source.getAttribute('href')).toBe(`/runs/${sourceId}`)
    await user().click(source)

    expect(await screen.findByRole('heading', { name: 'Исходный прогон' })).toBeTruthy()
    expect(calls.some((call) => call.method === 'GET' && call.url === `/api/runs/${sourceId}`)).toBe(true)
    expect(calls.filter((call) => call.method === 'POST')).toHaveLength(1)
  })

  it('ошибка GPT оставляет отчёт доступным и позволяет повторить запрос', async () => {
    let attempts = 0
    mockFetch((call) => {
      if (call.method !== 'POST')
        return json(200, { ...demoRun, id: ID })
      if (++attempts === 1)
        return json(504, { detail: 'OpenAI не ответил вовремя.' })
      return json(200, {
        hypothesis: 'Возможна очередь запросов.',
        facts: [{ step_index: 7, target_rps: 400, metric: 'error_rate_percent', value: 2.4 }],
        experiment: 'Измерьте длину очереди на той же нагрузке.',
      })
    })
    renderPage()

    const u = user()
    await u.click(await screen.findByRole('button', { name: 'Объяснить через GPT' }))
    expect((await screen.findByRole('alert')).textContent).toContain('OpenAI не ответил вовремя.')
    expect(screen.getByRole('heading', { name: 'Диагноз' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Ступени' })).toBeTruthy()

    await u.click(screen.getByRole('button', { name: 'Повторить запрос к GPT' }))
    const panel = within(screen.getByRole('region', { name: 'GPT-объяснение' }))
    expect((await panel.findByRole('listitem')).textContent).toContain('Доля ошибок 2,4 %')
    expect(panel.queryByRole('alert')).toBeNull()
  })
})
