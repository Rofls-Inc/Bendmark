import { Suspense, lazy } from 'react'
import { Link, NavLink, Route, Routes, useLocation } from 'react-router'
import { DEMO_RUN_ID } from './demo/demoRun'
import { useRecentRuns } from './run/recent'
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
  const recent = useRecentRuns()

  return (
    <div className="layout">
      <nav className="sidebar">
        <Link to="/" className="brand">Bendmark</Link>
        <NavLink to="/" end className="new-run">Новый прогон</NavLink>

        {recent.length > 0 && (
          <>
            <span className="nav-group">Недавние</span>
            {recent.map((run) => (
              <NavLink key={run.id} to={`/runs/${run.id}`} className="recent" title={run.id}>
                <span className={`dot status-${run.status}`} aria-hidden />
                <span className="recent-name">{run.name}</span>
              </NavLink>
            ))}
          </>
        )}

        <span className="nav-group">Демо</span>
        {demos.map((demo) => (
          <NavLink key={demo.id} to={`/runs/${demo.id}`}>{demo.label}</NavLink>
        ))}
      </nav>
      <main>
        <Routes>
          {/* key: каждый переход на форму начинает её заново, в том числе после «Повторить» */}
          <Route path="/" element={<ScenarioPage key={location.key} />} />
          <Route
            path="/runs/:id"
            element={<Suspense fallback={<p className="page muted">Загружаем…</p>}><RunPage /></Suspense>}
          />
          <Route path="*" element={<p className="page muted">Страница не найдена</p>} />
        </Routes>
      </main>
    </div>
  )
}
