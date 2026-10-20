import { useState } from 'react'
import { JobDetailPage } from './pages/JobDetailPage'
import { JobFormPage } from './pages/JobFormPage'
import { SearchPage } from './pages/SearchPage'
import { StatsPage } from './pages/StatsPage'
import type { Job } from './types'
import './styles.css'

type View =
  | { name: 'search' }
  | { name: 'detail'; id: string }
  | { name: 'create' }
  | { name: 'edit'; job: Job }
  | { name: 'stats' }

export default function App() {
  const [view, setView] = useState<View>({ name: 'search' })

  const goSearch = () => setView({ name: 'search' })

  return (
    <>
      <header className="topbar">
        <div className="topbar-inner">
          <div className="brand" onClick={goSearch}>
            Tech<span>Jobs</span>
          </div>
          <nav className="tabs">
            <button className={view.name === 'search' ? 'active' : ''} onClick={goSearch}>
              Vagas
            </button>
            <button className={view.name === 'stats' ? 'active' : ''} onClick={() => setView({ name: 'stats' })}>
              Estatísticas
            </button>
          </nav>
          <div style={{ flex: 1 }} />
          <button className="primary" onClick={() => setView({ name: 'create' })}>
            + Cadastrar vaga
          </button>
        </div>
      </header>

      <main className="container">
        {view.name === 'search' && (
          <SearchPage onOpenJob={(id) => setView({ name: 'detail', id })} />
        )}
        {view.name === 'detail' && (
          <JobDetailPage
            id={view.id}
            onBack={goSearch}
            onEdit={(job) => setView({ name: 'edit', job })}
          />
        )}
        {view.name === 'create' && (
          <JobFormPage editing={null} onCancel={goSearch} onSaved={goSearch} />
        )}
        {view.name === 'edit' && (
          <JobFormPage
            editing={view.job}
            onCancel={() => setView({ name: 'detail', id: view.job.id })}
            onSaved={(job) => setView({ name: 'detail', id: job.id })}
          />
        )}
        {view.name === 'stats' && <StatsPage />}
      </main>
    </>
  )
}
