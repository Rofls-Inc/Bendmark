// @vitest-environment jsdom
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import { ThemeSelect } from './ThemeSelect'

function mockSystemTheme(dark: boolean) {
  const media = Object.assign(new EventTarget(), { matches: dark })
  vi.stubGlobal('matchMedia', vi.fn(() => media))

  return (nextDark: boolean) => act(() => {
    media.matches = nextDark
    media.dispatchEvent(new Event('change'))
  })
}

function expectTheme(mode: string, active: string) {
  const radio = screen.getByRole('radio', { checked: true }) as HTMLInputElement
  expect(radio.value).toBe(mode)
  expect(document.documentElement.dataset.theme).toBe(active)
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  localStorage.clear()
  delete document.documentElement.dataset.theme
})

it('по умолчанию следит за темой системы', () => {
  const changeSystemTheme = mockSystemTheme(false)
  render(<ThemeSelect />)
  expectTheme('system', 'light')
  expect(window.matchMedia).toHaveBeenCalledWith('(prefers-color-scheme: dark)')

  changeSystemTheme(true)
  expectTheme('system', 'dark')
})

it.each([
  ['light', true],
  ['dark', false],
] as const)('восстанавливает сохранённую тему %s', (saved, systemDark) => {
  localStorage.setItem('bendmark.theme', saved)
  mockSystemTheme(systemDark)
  render(<ThemeSelect />)
  expectTheme(saved, saved)
})

it('игнорирует неизвестное сохранённое значение', () => {
  localStorage.setItem('bendmark.theme', 'unknown')
  mockSystemTheme(true)
  render(<ThemeSelect />)
  expectTheme('system', 'dark')
})

it('сохраняет выбор и следит за системой только в системном режиме', async () => {
  const changeSystemTheme = mockSystemTheme(true)
  render(<ThemeSelect />)
  await userEvent.click(screen.getByRole('radio', { name: 'Светлая' }))
  expectTheme('light', 'light')
  expect(localStorage.getItem('bendmark.theme')).toBe('light')
  changeSystemTheme(false)
  changeSystemTheme(true)
  expectTheme('light', 'light')

  await userEvent.click(screen.getByRole('radio', { name: 'Тёмная' }))
  expectTheme('dark', 'dark')
  expect(localStorage.getItem('bendmark.theme')).toBe('dark')
  changeSystemTheme(false)
  expectTheme('dark', 'dark')

  await userEvent.click(screen.getByRole('radio', { name: 'Системная' }))
  expectTheme('system', 'light')
  expect(localStorage.getItem('bendmark.theme')).toBe('system')
  changeSystemTheme(true)
  expectTheme('system', 'dark')
})

it('следит за системой при недоступном хранилище', async () => {
  const changeSystemTheme = mockSystemTheme(true)
  vi.spyOn(window, 'localStorage', 'get').mockImplementation(() => {
    throw new DOMException('Storage blocked', 'SecurityError')
  })
  render(<ThemeSelect />)
  expectTheme('system', 'dark')

  await userEvent.click(screen.getByRole('radio', { name: 'Светлая' }))
  expectTheme('system', 'dark')
  changeSystemTheme(false)
  expectTheme('system', 'light')
})
