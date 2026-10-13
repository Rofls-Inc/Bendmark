import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// Компонентные тесты идут в jsdom (// @vitest-environment jsdom), остальные в node
if (typeof window !== 'undefined') {
  afterEach(() => {
    cleanup()
    localStorage.clear()
  })

  // Recharts измеряет контейнер через ResizeObserver, которого в jsdom нет
  globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
}
