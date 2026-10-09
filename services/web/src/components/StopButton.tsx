import { useState } from 'react'
import { stopRun } from '../api/client'
import type { Run } from '../api/types'

interface Props {
  runId: string
  onStopped: (run: Run) => void
  disabled?: boolean
}

type State =
  | { kind: 'idle' }
  | { kind: 'confirm' }
  | { kind: 'stopping' }
  | { kind: 'pending' }
  | { kind: 'error'; message: string }

// Остановка в два нажатия: случайный клик не должен обрывать прогон.
// Координатор отвечает, когда агент снял нагрузку (обычно сразу, но до 15 с).
export function StopButton({ runId, onStopped, disabled }: Props) {
  const [state, setState] = useState<State>({ kind: 'idle' })

  const stop = async () => {
    setState({ kind: 'stopping' })
    try {
      const { run, pending } = await stopRun(runId)
      setState(pending ? { kind: 'pending' } : { kind: 'idle' })
      onStopped(run)
    } catch (e) {
      setState({ kind: 'error', message: e instanceof Error ? e.message : 'Не удалось остановить прогон' })
    }
  }

  if (state.kind === 'confirm')
    return (
      <div className="stop">
        <span className="muted">Остановить прогон?</span>
        <button type="button" className="danger" onClick={stop}>Остановить</button>
        <button type="button" onClick={() => setState({ kind: 'idle' })}>Отмена</button>
      </div>
    )

  return (
    <div className="stop">
      {state.kind === 'pending' && <span className="muted">Агент снимает нагрузку, итог появится здесь</span>}
      {state.kind === 'error' && <span className="error" role="alert">{state.message}</span>}
      <button
        type="button"
        className="danger"
        disabled={disabled || state.kind === 'stopping' || state.kind === 'pending'}
        onClick={() => setState({ kind: 'confirm' })}
      >
        {state.kind === 'stopping' ? 'Останавливаем…' : 'Остановить'}
      </button>
    </div>
  )
}
