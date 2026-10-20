import { useEffect, useState } from 'react'
import { getJob } from '../api'
import { ErrorBanner, Spinner, TechChips } from '../components/ui'
import { formatDate, formatSalary, type Job } from '../types'

export function JobDetailPage({ id, onEdit, onBack }: { id: string; onEdit: (job: Job) => void; onBack: () => void }) {
  const [job, setJob] = useState<Job | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)
    getJob(id)
      .then((j) => { if (!cancelled) setJob(j) })
      .catch((e) => { if (!cancelled) setError(e) })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [id])

  return (
    <div>
      <h1 className="page-title">Detalhe da vaga</h1>
      {error !== null && <ErrorBanner error={error} />}
      {loading && <Spinner label="Carregando vaga…" />}
      {job && (
        <div className="card">
          <div className="job-card-head">
            <div>
              <h2 style={{ margin: 0 }}>{job.title}</h2>
              <span className="company">{job.company}</span>
            </div>
            <div className="chips">
              <span className={`badge ${job.status}`}>{job.status}</span>
              <span className="badge meta">{job.seniority}</span>
              <span className="badge meta">{job.workMode}</span>
              {job.city && <span className="badge meta">{job.city}</span>}
            </div>
          </div>

          <TechChips technologies={job.technologies} />

          <div className="detail-section">
            <h4>Descrição</h4>
            <p>{job.description}</p>
          </div>

          <div className="detail-section">
            <h4>Remuneração</h4>
            <p>{formatSalary(job.salaryMin, job.salaryMax)}</p>
          </div>

          <div className="job-card-foot">
            <span>Publicada em {formatDate(job.publishedAt)} · Atualizada em {formatDate(job.updatedAt)}</span>
          </div>

          <div className="form-actions">
            <button className="primary" onClick={() => onEdit(job)}>Editar / Encerrar</button>
            <button className="ghost" onClick={onBack}>← Voltar para a busca</button>
          </div>
        </div>
      )}
    </div>
  )
}
