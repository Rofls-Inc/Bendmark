import { useEffect, useState } from 'react'
import { ApiError, getRun } from '../api/client'
import { FINAL_STATUSES, type Run } from '../api/types'
import { DEMO_RUN_ID, demoRun } from '../demo/demoRun'

export const POLL_INTERVAL_MS = 2000

export type RunState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; run: Run }

// Опрашивает GET /runs/{id}, пока прогон не дойдёт до конечного статуса
export function useRun(id: string): RunState {
  const [state, setState] = useState<{ id: string; value: RunState }>({ id, value: { kind: 'loading' } })

  useEffect(() => {
    if (id === DEMO_RUN_ID)
      return

    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined

    const poll = async () => {
      try {
        const run = await getRun(id, controller.signal)
        setState({ id, value: { kind: 'ready', run } })
        if (!FINAL_STATUSES.includes(run.status))
          timer = setTimeout(poll, POLL_INTERVAL_MS)
      } catch (e) {
        if (controller.signal.aborted)
          return
        const message = e instanceof Error ? e.message : 'Не удалось получить прогон'
        // Прогона нет (например, координатор перезапустился и потерял его): повторять бесполезно
        if (e instanceof ApiError && e.status === 404) {
          setState({ id, value: { kind: 'error', message } })
          return
        }
        setState((prev) => prev.id === id && prev.value.kind === 'ready'
          // Прогон уже показан: временный сбой связи не стирает экран, пробуем ещё раз
          ? prev
          : { id, value: { kind: 'error', message } })
        timer = setTimeout(poll, POLL_INTERVAL_MS)
      }
    }
    poll()

    return () => {
      controller.abort()
      clearTimeout(timer)
    }
  }, [id])

  if (id === DEMO_RUN_ID)
    return { kind: 'ready', run: demoRun }
  // Пока не пришёл ответ для нового id, не показываем прогон от прошлого
  return state.id === id ? state.value : { kind: 'loading' }
}
