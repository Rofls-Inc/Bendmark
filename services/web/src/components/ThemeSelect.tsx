import { useId, useLayoutEffect, useState } from 'react'

type Theme = 'system' | 'light' | 'dark'

const themes = [
  { mode: 'system', label: 'Системная', icon: 'M4 4h16a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1Zm8 12v4m-4 0h8' },
  { mode: 'light', label: 'Светлая', icon: 'M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5m-2.5 7a4.5 4.5 0 1 1-9 0 4.5 4.5 0 0 1 9 0Z' },
  { mode: 'dark', label: 'Тёмная', icon: 'M20.8 14.3A9 9 0 0 1 9.7 3.2 9 9 0 1 0 20.8 14.3Z' },
] as const

// Эти значения также использует стартовый скрипт в index.html.
const themeKey = 'bendmark.theme'
const darkPreference = '(prefers-color-scheme: dark)'

function readTheme(): Theme {
  try {
    const saved = localStorage.getItem(themeKey)
    if (saved === 'light' || saved === 'dark') return saved
  } catch {
    // Хранилище может быть заблокировано настройками браузера.
  }
  return 'system'
}

export function ThemeSelect() {
  const [theme, setTheme] = useState(readTheme)
  const labelId = useId()

  useLayoutEffect(() => {
    if (theme !== 'system') {
      document.documentElement.dataset.theme = theme
      return
    }

    const media = window.matchMedia?.(darkPreference)
    const applyTheme = () => {
      document.documentElement.dataset.theme = (media?.matches ?? true) ? 'dark' : 'light'
    }
    applyTheme()
    media?.addEventListener('change', applyTheme)
    return () => media?.removeEventListener('change', applyTheme)
  }, [theme])

  function changeTheme(next: Theme) {
    try {
      localStorage.setItem(themeKey, next)
      setTheme(next)
    } catch {
      setTheme('system')
    }
  }

  return (
    <div className="theme-select">
      <span id={labelId}>Тема</span>
      <div className="theme-options" role="radiogroup" aria-labelledby={labelId}>
        {themes.map(({ mode, label, icon }) => (
          <label key={mode} title={label}>
            <input type="radio" name={labelId} value={mode} aria-label={label}
              checked={theme === mode} onChange={() => changeTheme(mode)} />
            <span>
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" aria-hidden="true">
                <path d={icon} stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </span>
          </label>
        ))}
      </div>
    </div>
  )
}
