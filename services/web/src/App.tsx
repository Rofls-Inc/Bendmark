import { Suspense, lazy, useState } from 'react'
import { Link, NavLink, Route, Routes, useLocation } from 'react-router'
import type { RunSummary } from './api/types'
import { Logo } from './components/Logo'
import { State } from './components/State'
import { DEMO_RUN_ID } from './demo/demoRun'
import { refreshRecent, useRecentRuns } from './run/recent'
import { formatNumber } from './run/view'
import { ScenarioPage } from './scenario/ScenarioPage'

// Графики тяжёлые, грузим их только на странице прогона
const RunPage = lazy(() => import('./run/RunPage').then((m) => ({ default: m.RunPage })))

const demos = [
  { id: DEMO_RUN_ID, label: 'Предел найден' },
  { id: 'demo-live', label: 'Идёт прогон' },
  { id: 'demo-aborted', label: 'Остановлен' },
  { id: 'demo-failed', label: 'Сбой' },
  { id: 'demo-skipped', label: 'С пропусками' },
]

export function App() {
  const location = useLocation()

  return (
    <div className="layout">
      <Sidebar />
      <main>
        <Routes>
          {/* key: каждый переход на форму начинает её заново, в том числе после «Повторить» */}
          <Route path="/" element={<ScenarioPage key={location.key} />} />
          <Route
            path="/runs/:id"
            element={
              <Suspense fallback={<div className="page"><State kind="loading" title="Загружаем…" /></div>}>
                <RunPage />
              </Suspense>
            }
          />
          <Route
            path="*"
            element={
              <div className="page">
                <State kind="empty" title="Страница не найдена">
                  <Link to="/">К новому прогону</Link>
                </State>
              </div>
            }
          />
        </Routes>
      </main>
    </div>
  )
}

function Sidebar() {
  const recent = useRecentRuns()
  const location = useLocation()
  // Меню на узком экране; на широком список виден всегда.
  // Помним, на какой странице его открыли: после перехода оно закрыто
  const [openedAt, setOpenedAt] = useState<string | null>(null)
  const open = openedAt === location.key

  return (
    <nav className={`sidebar${open ? ' open' : ''}`} aria-label="Навигация">
      <div className="sidebar-head">
        <Link to="/" className="brand"><Logo />Bendmark</Link>
        <button
          type="button"
          className="menu-toggle"
          aria-expanded={open}
          aria-controls="sidebar-nav"
          onClick={() => setOpenedAt(open ? null : location.key)}
        >
          {open ? 'Закрыть' : 'Меню'}
        </button>
      </div>

      <div className="sidebar-nav" id="sidebar-nav">
        <NavLink to="/" end className="new-run">Новый прогон</NavLink>

        <span className="nav-group">Недавние</span>
        {recent.kind === 'loading' && <span className="nav-note">Загружаем…</span>}
        {recent.kind === 'error' && (
          <span className="nav-note error">
            {recent.message}{' '}
            <button type="button" className="link" onClick={() => refreshRecent()}>Повторить</button>
          </span>
        )}
        {recent.kind === 'ready' && recent.runs.length === 0 && <span className="nav-note">Прогонов пока нет</span>}
        {recent.kind === 'ready' && recent.runs.map((run) => (
          <NavLink key={run.id} to={`/runs/${run.id}`} title={run.id}>
            <span className={`dot status-${run.status}`} aria-hidden />
            <span className="recent-name">{run.scenario_name}</span>
            <span className={`recent-meta status-${run.status}`} aria-hidden>{recentMeta(run)}</span>
          </NavLink>
        ))}

        <span className="nav-group">Демо</span>
        {demos.map((demo) => (
          <NavLink key={demo.id} to={`/runs/${demo.id}`}>
            <span className="recent-name">{demo.label}</span>
          </NavLink>
        ))}
      </div>

      <p className="sidebar-foot">Bendmark · учебный проект СПбГУ</p>
    </nav>
  )
}

// Короткая подпись справа: предел, если он есть, иначе состояние
function recentMeta(run: RunSummary): string {
  switch (run.status) {
    case 'created':
      return 'очередь'
    case 'running':
      return 'идёт'
    case 'failed':
      return 'сбой'
    case 'aborted':
      return 'стоп'
    case 'completed':
      if (run.limit_rps === null)
        return ''
      return run.analysis_status === 'not_found' ? `≥${formatNumber(run.limit_rps)}` : formatNumber(run.limit_rps)
  }
}
