import type { ReactNode } from 'react'
import { ApiError } from '../api'
import type { SearchItem } from '../types'
import { formatDate, formatSalary } from '../types'

export function Spinner({ label = 'Carregando…' }: { label?: string }) {
  return (
    <div className="spinner-wrap">
      <div className="spinner" aria-hidden />
      <span>{label}</span>
    </div>
  )
}

export function ErrorBanner({ error }: { error: unknown }) {
  if (!(error instanceof ApiError)) {
    return (
      <div className="error-banner" role="alert">
        <div className="title">Erro inesperado</div>
        <div>{error instanceof Error ? error.message : String(error)}</div>
      </div>
    )
  }
  return (
    <div className="error-banner" role="alert">
      <div className="title">{error.title} (HTTP {error.status})</div>
      <div>{error.message}</div>
    </div>
  )
}

export function TechChips({ technologies }: { technologies: string[] }) {
  return (
    <div className="chips">
      {technologies.map((tech) => (
        <span key={tech} className="chip">{tech}</span>
      ))}
    </div>
  )
}

export function JobCard({ job, onOpen }: { job: SearchItem; onOpen: (id: string) => void }) {
  return (
    <article className="card job-card">
      <button
        type="button"
        className="job-card-open"
        aria-label={`Abrir vaga: ${job.title}`}
        onClick={() => onOpen(job.id)}
      />
      <div className="job-card-head">
        <div>
          <h3>{job.title}</h3>
          <span className="company">{job.company}</span>
        </div>
        <div className="chips">
          <span className="badge meta">{job.seniority}</span>
          <span className="badge meta">{job.workMode}</span>
          {job.city && <span className="badge meta">{job.city}</span>}
        </div>
      </div>
      <TechChips technologies={job.technologies} />
      <div className="job-card-foot">
        <span>{formatSalary(job.salaryMin, job.salaryMax)}</span>
        <span>Publicada em {formatDate(job.publishedAt)}</span>
      </div>
    </article>
  )
}

export function Pagination({
  page, pageSize, total, onPage,
}: { page: number; pageSize: number; total: number; onPage: (page: number) => void }) {
  const lastPage = Math.max(1, Math.ceil(total / pageSize))
  return (
    <div className="pagination">
      <button className="ghost" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        ← Anterior
      </button>
      <span className="info">Página {page} de {lastPage}</span>
      <button className="ghost" disabled={page >= lastPage} onClick={() => onPage(page + 1)}>
        Próxima →
      </button>
    </div>
  )
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field">
      {label}
      {children}
    </label>
  )
}
