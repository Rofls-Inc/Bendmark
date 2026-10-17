// @vitest-environment jsdom
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { App } from './App'
import { json, mockFetch } from './test/fetch'

function renderApp() {
  return render(<MemoryRouter><App /></MemoryRouter>)
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('боковая панель «Недавние»', () => {
  it('показывает прогоны из GET /runs со статусом координатора', async () => {
    const calls = mockFetch(() => json(200, {
      runs: [
        { id: 'r2', scenario_name: 'checkout', status: 'running', created_at: '2026-10-09T12:05:00Z',
          started_at: '2026-10-09T12:05:01Z', finished_at: null, analysis_status: null, limit_rps: null },
        { id: 'r1', scenario_name: 'abstock-home-ramp', status: 'completed', created_at: '2026-10-09T12:00:00Z',
          started_at: '2026-10-09T12:00:01Z', finished_at: '2026-10-09T12:04:00Z', analysis_status: 'found', limit_rps: 350 },
      ],
      next_before: null,
    }))
    renderApp()

    const link = await screen.findByRole('link', { name: 'checkout' })
    expect(link.getAttribute('href')).toBe('/runs/r2')
    expect(link.querySelector('.status-running')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'abstock-home-ramp' }).querySelector('.status-completed')).toBeTruthy()
    expect(link.textContent).toContain('идёт')
    expect(screen.getByRole('link', { name: 'abstock-home-ramp' }).textContent).toContain('350')
    expect(calls[0]).toMatchObject({ method: 'GET', url: '/api/runs?limit=10' })
  })

  it('пустой список', async () => {
    mockFetch(() => json(200, { runs: [], next_before: null }))
    renderApp()
    expect(await screen.findByText('Прогонов пока нет')).toBeTruthy()
  })

  it('координатор недоступен', async () => {
    mockFetch(() => {
      throw new TypeError('Failed to fetch')
    })
    renderApp()
    expect(await screen.findByText('Координатор недоступен')).toBeTruthy()
  })

  it('после ошибки список можно перечитать кнопкой', async () => {
    let up = false
    mockFetch(() => up
      ? json(200, { runs: [], next_before: null })
      : new Response('<html>Bad Gateway</html>', { status: 502 }))
    renderApp()

    expect(await screen.findByText(/Координатор недоступен/)).toBeTruthy()
    up = true
    await userEvent.click(screen.getByRole('button', { name: 'Повторить' }))
    expect(await screen.findByText('Прогонов пока нет')).toBeTruthy()
  })
})
