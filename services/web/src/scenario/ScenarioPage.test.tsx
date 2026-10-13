// @vitest-environment jsdom
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useParams } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Scenario } from '../api/types'
import { loadRecent } from '../run/recent'
import { json, mockFetch } from '../test/fetch'
import { ScenarioPage } from './ScenarioPage'

function RunStub() {
  const { id } = useParams()
  return <p>страница прогона {id}</p>
}

function renderPage(state?: { scenario: Scenario }) {
  return render(
    <MemoryRouter initialEntries={[{ pathname: '/', state }]}>
      <Routes>
        <Route path="/" element={<ScenarioPage />} />
        <Route path="/runs/:id" element={<RunStub />} />
      </Routes>
    </MemoryRouter>,
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('ScenarioPage', () => {
  it('создаёт прогон и переходит на его страницу', async () => {
    const calls = mockFetch(() => json(201, { id: 'run-1', status: 'created' }))
    renderPage()

    await userEvent.click(screen.getByRole('button', { name: 'Запустить' }))

    expect(await screen.findByText('страница прогона run-1')).toBeTruthy()
    expect(calls).toHaveLength(1)
    expect(calls[0]).toMatchObject({ method: 'POST', url: '/api/runs' })
    expect(calls[0].body).toMatchObject({ name: 'abstock-home-ramp', target: { method: 'GET' } })
    expect((calls[0].body as Scenario).steps).toHaveLength(7)
    expect(loadRecent()[0]).toMatchObject({ id: 'run-1', name: 'abstock-home-ramp' })
  })

  it('ошибки 400 показывает у полей', async () => {
    mockFetch(() => json(400, {
      errors: {
        'target.url': ['Нужен абсолютный URL с http или https'],
        'steps[2].target_rps': ['Нагрузка должна расти: 150 после 200'],
      },
    }))
    renderPage()

    await userEvent.click(screen.getByRole('button', { name: 'Запустить' }))

    expect(await screen.findByText('Нужен абсолютный URL с http или https')).toBeTruthy()
    expect(screen.getByText('Ступень 3: Нагрузка должна расти: 150 после 200')).toBeTruthy()
    // Исправили поле - его ошибка ушла
    await userEvent.type(screen.getByDisplayValue('http://abstock:8080/'), 'x')
    expect(screen.queryByText('Нужен абсолютный URL с http или https')).toBeNull()
  })

  it('ошибка сети - общим сообщением', async () => {
    mockFetch(() => json(502, {}))
    renderPage()
    await userEvent.click(screen.getByRole('button', { name: 'Запустить' }))
    expect(await screen.findByText('Координатор ответил 502')).toBeTruthy()
  })

  it('неверное значение блокирует запуск без запроса', async () => {
    const calls = mockFetch(() => json(201, { id: 'x', status: 'created' }))
    renderPage()
    const step = screen.getByDisplayValue('50')
    await userEvent.clear(step)
    expect(screen.getByText('Нужно число больше нуля')).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Запустить' }) as HTMLButtonElement).disabled).toBe(true)
    expect(calls).toHaveLength(0)
  })

  it('«Повторить» заполняет форму сценарием прогона', () => {
    renderPage({
      scenario: {
        name: 'checkout',
        target: { url: 'http://shop:8080/cart', method: 'GET' },
        steps: [10, 25, 40].map((target_rps) => ({ target_rps, duration_seconds: 5 })),
      },
    })
    expect(screen.getByDisplayValue('checkout')).toBeTruthy()
    expect(screen.getByDisplayValue('http://shop:8080/cart')).toBeTruthy()
    expect(screen.getByDisplayValue('10')).toBeTruthy()
    expect(screen.getByDisplayValue('40')).toBeTruthy()
    expect(screen.getByDisplayValue('15')).toBeTruthy()
    expect(screen.getByDisplayValue('5')).toBeTruthy()
    expect(screen.queryByText(/не укладываются/)).toBeNull()
  })
})
