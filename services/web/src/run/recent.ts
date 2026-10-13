import { useSyncExternalStore } from 'react'
import type { Run, RunStatus } from '../api/types'

// Недавние прогоны хранятся в браузере: у координатора нет списка прогонов.
// Хранилище может быть недоступно (приватное окно, запрет cookies) - тогда список просто пустой.

export interface RecentRun {
  id: string
  name: string
  status: RunStatus
  created_at: string
}

export const RECENT_KEY = 'bendmark.recent-runs'
export const RECENT_LIMIT = 10

const listeners = new Set<() => void>()
let cache: { raw: string | null; runs: RecentRun[] } = { raw: null, runs: [] }

function readRaw(): string | null {
  try {
    return localStorage.getItem(RECENT_KEY)
  } catch {
    return null
  }
}

export function loadRecent(): RecentRun[] {
  const raw = readRaw()
  // Тот же массив для той же строки: useSyncExternalStore требует стабильный снимок
  if (raw === cache.raw)
    return cache.runs
  cache = { raw, runs: parse(raw) }
  return cache.runs
}

function parse(raw: string | null): RecentRun[] {
  try {
    const parsed: unknown = raw ? JSON.parse(raw) : []
    return Array.isArray(parsed) ? parsed.filter(isRecentRun).slice(0, RECENT_LIMIT) : []
  } catch {
    return []
  }
}

function save(runs: RecentRun[]) {
  try {
    localStorage.setItem(RECENT_KEY, JSON.stringify(runs))
  } catch {
    // Не сохранилось - не страшно, это только удобство
  }
  listeners.forEach((notify) => notify())
}

// Добавляет прогон в начало списка или обновляет его статус
export function rememberRun(run: Pick<Run, 'id' | 'status' | 'created_at'> & { name: string }) {
  const current = loadRecent()
  const existing = current.find((r) => r.id === run.id)
  if (existing && existing.status === run.status && existing.name === run.name)
    return
  const entry: RecentRun = { id: run.id, name: run.name, status: run.status, created_at: run.created_at }
  const next = existing
    ? current.map((r) => (r.id === run.id ? entry : r))
    : [entry, ...current].slice(0, RECENT_LIMIT)
  save(next)
}

export function forgetRun(id: string) {
  const current = loadRecent()
  if (current.some((r) => r.id === id))
    save(current.filter((r) => r.id !== id))
}

function subscribe(notify: () => void) {
  listeners.add(notify)
  // Другие вкладки тоже могут менять список
  const onStorage = (e: StorageEvent) => {
    if (e.key === RECENT_KEY)
      notify()
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(notify)
    window.removeEventListener('storage', onStorage)
  }
}

export function useRecentRuns(): RecentRun[] {
  return useSyncExternalStore(subscribe, loadRecent, () => [])
}

function isRecentRun(value: unknown): value is RecentRun {
  if (value === null || typeof value !== 'object')
    return false
  const r = value as Record<string, unknown>
  return typeof r.id === 'string' && typeof r.name === 'string'
    && typeof r.status === 'string' && typeof r.created_at === 'string'
}
