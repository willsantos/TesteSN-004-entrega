import { useEffect, useState } from 'react'
import { getStats } from '../api'
import { ErrorBanner, Spinner } from '../components/ui'
import type { JobsStats } from '../types'

function Bar({ value, max }: { value: number; max: number }) {
  const pct = max > 0 ? Math.round((value / max) * 100) : 0
  return (
    <div className="bar" aria-hidden>
      <div style={{ width: `${pct}%` }} />
    </div>
  )
}

export function StatsPage() {
  const [stats, setStats] = useState<JobsStats | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)
    getStats()
      .then((s) => { if (!cancelled) setStats(s) })
      .catch((e) => { if (!cancelled) setError(e) })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [])

  return (
    <div>
      <h1 className="page-title">Estatísticas do catálogo</h1>
      {error !== null && <ErrorBanner error={error} />}
      {loading && <Spinner label="Calculando estatísticas…" />}

      {stats && (
        <>
          <div className="total-banner">
            <div className="number">{stats.totalOpenJobs.toLocaleString('pt-BR')}</div>
            <div className="label">vagas abertas no catálogo (agregações em tempo real do índice de busca)</div>
          </div>

          <div className="stats-grid">
            <div className="card">
              <h3 style={{ marginTop: 0 }}>Por senioridade</h3>
              {Object.entries(stats.bySeniority).map(([key, count]) => (
                <div key={key} className="stat-row">
                  <span>{key}</span>
                  <span className="count">{count.toLocaleString('pt-BR')}</span>
                </div>
              ))}
              <Bar value={Math.max(...Object.values(stats.bySeniority), 1)} max={Math.max(...Object.values(stats.bySeniority), 1)} />
            </div>

            <div className="card">
              <h3 style={{ marginTop: 0 }}>Por modalidade</h3>
              {Object.entries(stats.byWorkMode).map(([key, count]) => (
                <div key={key} className="stat-row">
                  <span>{key}</span>
                  <span className="count">{count.toLocaleString('pt-BR')}</span>
                </div>
              ))}
            </div>

            <div className="card" style={{ gridColumn: '1 / -1' }}>
              <h3 style={{ marginTop: 0 }}>Top tecnologias</h3>
              <p style={{ color: 'var(--muted)', fontSize: '0.85rem', marginTop: 0 }}>
                Uma vaga pode listar várias tecnologias, então a soma ultrapassa o total de vagas abertas.
              </p>
              {stats.topTechnologies.map((tech) => (
                <div key={tech.name} className="stat-row">
                  <span>{tech.name}</span>
                  <span style={{ display: 'flex', alignItems: 'center', gap: 10, width: '55%', justifyContent: 'flex-end' }}>
                    <span style={{ flex: 1 }}><Bar value={tech.count} max={stats.topTechnologies[0]?.count ?? 1} /></span>
                    <span className="count">{tech.count.toLocaleString('pt-BR')}</span>
                  </span>
                </div>
              ))}
            </div>
          </div>
        </>
      )}
    </div>
  )
}
