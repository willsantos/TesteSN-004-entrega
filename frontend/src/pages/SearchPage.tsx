import { useEffect, useState } from 'react'
import { searchJobs } from '../api'
import { ErrorBanner, Field, JobCard, Pagination, Spinner } from '../components/ui'
import { emptyFilters, type SearchFilters, type SearchResult, type SortOption } from '../types'

const sorts: { value: SortOption; label: string }[] = [
  { value: 'relevance', label: 'Relevância' },
  { value: 'newest', label: 'Mais recentes' },
  { value: 'salary_desc', label: 'Maior salário' },
]

export function SearchPage({
  filters, onFiltersChange, onOpenJob,
}: {
  filters: SearchFilters
  onFiltersChange: (filters: SearchFilters) => void
  onOpenJob: (id: string) => void
}) {
  // Rascunho do formulário, inicializado a partir dos filtros aplicados
  // (sobrevive à remontagem ao voltar do detalhe).
  const [draft, setDraft] = useState<SearchFilters>(filters)
  const [result, setResult] = useState<SearchResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)
    searchJobs(filters)
      .then((r) => { if (!cancelled) setResult(r) })
      .catch((e) => { if (!cancelled) { setResult(null); setError(e) } })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [filters])

  // Aplicar filtros explicitamente volta para a página 1.
  const apply = (next: SearchFilters) => onFiltersChange({ ...next, page: 1 })

  const set = (patch: Partial<SearchFilters>) => setDraft((d) => ({ ...d, ...patch }))

  return (
    <div>
      <h1 className="page-title">Buscar vagas</h1>

      <form
        className="card filters"
        onSubmit={(e) => { e.preventDefault(); apply(draft) }}
      >
        <Field label="Busca livre (título e descrição)">
          <input
            value={draft.q}
            onChange={(e) => set({ q: e.target.value })}
            placeholder="ex.: desenvolvedor backend"
          />
        </Field>
        <Field label="Senioridade">
          <select value={draft.seniority} onChange={(e) => set({ seniority: e.target.value })}>
            <option value="">Todas</option>
            <option value="junior">Júnior</option>
            <option value="pleno">Pleno</option>
            <option value="senior">Sênior</option>
          </select>
        </Field>
        <Field label="Modalidade">
          <select value={draft.workMode} onChange={(e) => set({ workMode: e.target.value })}>
            <option value="">Todas</option>
            <option value="remoto">Remoto</option>
            <option value="hibrido">Híbrido</option>
            <option value="presencial">Presencial</option>
          </select>
        </Field>
        <Field label="Tecnologias (CSV, todas devem constar)">
          <input
            value={draft.technologies}
            onChange={(e) => set({ technologies: e.target.value })}
            placeholder="ex.: Redis,C#"
          />
        </Field>
        <Field label="Salário mínimo (R$)">
          <input
            inputMode="decimal"
            value={draft.minSalary}
            onChange={(e) => set({ minSalary: e.target.value })}
            placeholder="ex.: 8000"
          />
        </Field>
        <Field label="Salário máximo (R$)">
          <input
            inputMode="decimal"
            value={draft.maxSalary}
            onChange={(e) => set({ maxSalary: e.target.value })}
            placeholder="ex.: 15000"
          />
        </Field>
        <Field label="Ordenação">
          <select value={draft.sort} onChange={(e) => set({ sort: e.target.value as SortOption })}>
            {sorts.map((s) => (
              <option key={s.value} value={s.value}>{s.label}</option>
            ))}
          </select>
        </Field>
        <Field label="Resultados por página">
          <select
            value={String(draft.pageSize)}
            onChange={(e) => set({ pageSize: Number(e.target.value) })}
          >
            {[10, 20, 50].map((n) => (
              <option key={n} value={n}>{n}</option>
            ))}
          </select>
        </Field>
        <div className="span-all" style={{ display: 'flex', gap: 10 }}>
          <button className="primary" type="submit">Buscar</button>
          <button
            className="ghost"
            type="button"
            onClick={() => { setDraft(emptyFilters); apply(emptyFilters) }}
          >
            Limpar
          </button>
        </div>
      </form>

      {error !== null && <ErrorBanner error={error} />}
      {loading && <Spinner label="Buscando vagas…" />}

      {!loading && error === null && result && (
        <>
          <div className="result-meta">
            {result.total === 0
              ? 'Nenhuma vaga encontrada com esses filtros.'
              : `${result.total} vaga(s) aberta(s) encontrada(s) — mostrando página ${result.page}.`}
          </div>
          <div className="job-list">
            {result.items.map((job) => (
              <JobCard key={job.id} job={job} onOpen={onOpenJob} />
            ))}
          </div>
          {result.total > result.pageSize && (
            <Pagination
              page={result.page}
              pageSize={result.pageSize}
              total={result.total}
              onPage={(page) => onFiltersChange({ ...filters, page })}
            />
          )}
        </>
      )}
    </div>
  )
}
