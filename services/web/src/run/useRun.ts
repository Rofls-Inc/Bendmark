import { useCallback, useEffect, useMemo, useState } from 'react'
import { ApiError, getRun } from '../api/client'
import { FINAL_STATUSES, type Run } from '../api/types'
import { demoRuns, isDemoRun } from '../demo/demoRun'
import { rememberRun } from './recent'

export const POLL_INTERVAL_MS = 2000

export type RunState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; run: Run }

// Опрашивает GET /runs/{id}, пока прогон не дойдёт до конечного статуса.
// applyRun сразу показывает прогон из другого ответа (например, из POST /stop)
// и возобновляет опрос, если прогон ещё не завершён.
export function useRun(id: string): { state: RunState; applyRun: (run: Run) => void } {
  const [state, setState] = useState<{ id: string; value: RunState }>({ id, value: { kind: 'loading' } })
  // Меняется, когда нужно перезапустить опрос после applyRun
  const [generation, setGeneration] = useState(0)
  // Демо собирается один раз на id: у живого демо время старта считается от момента открытия
  const demo = useMemo(() => (isDemoRun(id) ? demoRuns[id]() : null), [id])

  useEffect(() => {
    if (isDemoRun(id))
      return

    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined

    const poll = async () => {
      try {
        const run = await getRun(id, controller.signal)
        setState({ id, value: { kind: 'ready', run } })
        rememberRun({ ...run, name: run.scenario.name })
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
  }, [id, generation])

  const applyRun = useCallback((run: Run) => {
    if (run.id !== id)
      return
    setState({ id, value: { kind: 'ready', run } })
    rememberRun({ ...run, name: run.scenario.name })
    setGeneration((g) => g + 1)
  }, [id])

  if (demo)
    return { state: { kind: 'ready', run: demo }, applyRun }
  // Пока не пришёл ответ для нового id, не показываем прогон от прошлого
  return { state: state.id === id ? state.value : { kind: 'loading' }, applyRun }
}
