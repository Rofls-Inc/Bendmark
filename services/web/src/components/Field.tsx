import type { ReactNode } from 'react'

interface FieldProps {
  label: string
  unit?: string
  hint?: string
  error?: string
  warning?: string
  children: ReactNode
}

export function Field({ label, unit, hint, error, warning, children }: FieldProps) {
  const state = error ? ' invalid' : warning ? ' warning' : ''
  return (
    <label className={`field${state}`}>
      <span className="label">{label}</span>
      <span className="control">
        {children}
        {unit && <span className="unit">{unit}</span>}
      </span>
      {error
        ? <span className="error">{error}</span>
        : warning
          ? <span className="warning-text">{warning}</span>
          : hint && <span className="hint">{hint}</span>}
    </label>
  )
}
