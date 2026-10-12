import { Suspense, lazy } from 'react'
import { Link, NavLink, Route, Routes } from 'react-router'
import { DEMO_RUN_ID } from './demo/demoRun'
import { ScenarioPage } from './scenario/ScenarioPage'

// Графики тяжёлые, грузим их только на странице прогона
const RunPage = lazy(() => import('./run/RunPage').then((m) => ({ default: m.RunPage })))

export function App() {
  return (
    <div className="layout">
      <nav className="sidebar">
        <Link to="/" className="brand">Bendmark</Link>
        <NavLink to="/" end>Новый прогон</NavLink>
        <NavLink to={`/runs/${DEMO_RUN_ID}`}>Демо-отчёт</NavLink>
      </nav>
      <main>
        <Routes>
          <Route path="/" element={<ScenarioPage />} />
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
