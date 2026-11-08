import { useLayoutEffect, useState } from 'react'

type Theme = 'system' | 'light' | 'dark'

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
      <span>Тема</span>
      <select aria-label="Тема" value={theme} onChange={(event) => changeTheme(event.target.value as Theme)}>
        <option value="system">Системная</option>
        <option value="light">Светлая</option>
        <option value="dark">Тёмная</option>
      </select>
    </div>
  )
}
