// @vitest-environment jsdom
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, expect, it, vi } from 'vitest'
import type { EmulatorConfiguration } from '../api/types'
import { json, mockFetch } from '../test/fetch'
import { EmulatorLabPage } from './EmulatorLabPage'

const initial: EmulatorConfiguration = {
  target_url: 'http://emulator:8080/work',
  capacity_rps: 0,
  settings: {
    mode: 'healthy', service_time: '250ms', workers: 3, queue_size: 32, slow_delay: '5s',
    jitter: '0s', outlier_rate: '0', outlier_delay: '0s', seed: '1',
  },
}

afterEach(() => vi.unstubAllGlobals())

it('применяет режим через API и начинает прогон с адресом эмулятора', async () => {
  const calls = mockFetch((call) => {
    if (call.method === 'GET')
      return json(200, initial)
    if (call.method === 'PUT')
      return json(200, { ...initial, capacity_rps: 12, settings: { ...initial.settings, mode: 'saturation' } })
    if (call.method === 'POST')
      return json(201, { id: 'lab-run', status: 'created' })
    throw new Error('unexpected HTTP request')
  })
  render(
    <MemoryRouter initialEntries={['/lab']}>
      <Routes>
        <Route path="/lab" element={<EmulatorLabPage />} />
        <Route path="/runs/:id" element={<RunPath />} />
      </Routes>
    </MemoryRouter>,
  )
  const user = userEvent.setup()
  await user.click(await screen.findByRole('radio', { name: /Насыщение/ }))
  const launch = screen.getByRole('button', { name: 'Запустить пример' }) as HTMLButtonElement
  expect(launch.disabled).toBe(true)
  await user.click(screen.getByRole('button', { name: 'Применить настройки' }))
  expect(await screen.findByText('Настройки применены')).toBeTruthy()
  expect(screen.getByText('6 · 9 · 12 · 15 · 18')).toBeTruthy()
  expect(calls[1]).toMatchObject({ method: 'PUT', url: '/api/emulator/config' })
  expect(calls[1].body).toMatchObject({ mode: 'saturation', service_time: '250ms' })
  await user.click(launch)
  expect(await screen.findByText('/runs/lab-run')).toBeTruthy()
  expect(calls[2]).toMatchObject({ method: 'POST', url: '/api/runs' })
  expect(calls[2].body).toMatchObject({
    target: { url: initial.target_url, method: 'GET' },
    steps: [6, 9, 12, 15, 18].map((target_rps) => ({ target_rps, duration_seconds: 10 })),
  })
})

function RunPath() {
  return <p>{useLocation().pathname}</p>
}
