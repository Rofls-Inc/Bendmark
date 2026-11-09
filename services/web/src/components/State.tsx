import type { ReactNode } from 'react'

interface Props {
  kind: 'loading' | 'empty' | 'error'
  title: string
  children?: ReactNode
}

// Загрузка, пустой результат и ошибка выглядят одинаково на всех экранах
export function State({ kind, title, children }: Props) {
  return (
    <div className={`state ${kind}`} role={kind === 'error' ? 'alert' : 'status'}>
      <p className="state-title">
        {kind === 'loading' && <span className="spinner" aria-hidden />}
        {title}
      </p>
      {children}
    </div>
  )
}
