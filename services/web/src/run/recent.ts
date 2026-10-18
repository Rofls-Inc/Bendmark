import { useEffect, useSyncExternalStore } from 'react'
import { listRuns } from '../api/client'
import type { RunSummary } from '../api/types'

// Недавние прогоны - первая страница GET /runs: одна на все браузеры, статусы от координатора.
// Список обновляется при открытии приложения, при возврате в окно, после создания прогона
// и при смене его статуса (useRun).

export const RECENT_LIMIT = 10

export type RecentState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; runs: RunSummary[] }

const INITIAL: RecentState = { kind: 'loading' }

let state: RecentState = INITIAL
// Номер последнего запроса: ответ на более ранний, пришедший позже, не затирает свежий
let latest = 0
const listeners = new Set<() => void>()

function set(next: RecentState) {
  state = next
  listeners.forEach((notify) => notify())
}

export async function refreshRecent(): Promise<void> {
  const current = ++latest
  try {
    const { runs } = await listRuns({ limit: RECENT_LIMIT })
    if (current === latest)
      set({ kind: 'ready', runs })
  } catch (e) {
    // Список уже показан: сбой связи не стирает его, обновим при следующем поводе
    if (current !== latest || state.kind === 'ready')
      return
    set({ kind: 'error', message: e instanceof Error ? e.message : 'Не удалось загрузить прогоны' })
  }
}

export function loadRecent(): RecentState {
  return state
}

// Для тестов: каждый начинает с пустого списка
export function resetRecent() {
  latest++
  state = INITIAL
}

function subscribe(notify: () => void) {
  listeners.add(notify)
  return () => {
    listeners.delete(notify)
  }
}

export function useRecentRuns(): RecentState {
  useEffect(() => {
    refreshRecent()
    const onFocus = () => {
      refreshRecent()
    }
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [])
  return useSyncExternalStore(subscribe, loadRecent, () => INITIAL)
}
