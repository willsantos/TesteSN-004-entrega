import type { Job, JobInput, JobsStats, SearchFilters, SearchResult } from './types'

// Cliente HTTP da API. A URL base pode ser sobrescrita em build com VITE_API_URL.
export const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:8080'

/** Erro de API com detalhe de Problem Details (RFC 9457) quando disponível. */
export class ApiError extends Error {
  readonly status: number
  readonly title: string
  readonly type: string

  constructor(status: number, title: string, detail: string, type: string) {
    super(detail || title)
    this.status = status
    this.title = title
    this.type = type
  }
}

async function parseError(response: Response): Promise<ApiError> {
  let title = `Erro ${response.status}`
  let detail = response.statusText || 'Falha inesperada na chamada da API.'
  let type = 'unknown'
  try {
    const body = await response.json()
    if (body?.title) title = body.title
    if (body?.detail) detail = body.detail
    if (body?.type) type = body.type
  } catch {
    // resposta sem corpo JSON — mantém defaults
  }
  return new ApiError(response.status, title, detail, type)
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(`${API_URL}${path}`, init)
  } catch {
    throw new ApiError(0, 'API indisponível', `Não foi possível conectar em ${API_URL}. Verifique se a stack está no ar (docker compose up).`, 'network')
  }
  if (!response.ok) throw await parseError(response)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export function searchJobs(filters: SearchFilters): Promise<SearchResult> {
  const params = new URLSearchParams()
  if (filters.q.trim()) params.set('q', filters.q.trim())
  if (filters.seniority) params.set('seniority', filters.seniority)
  if (filters.workMode) params.set('workMode', filters.workMode)
  if (filters.technologies.trim()) params.set('technologies', filters.technologies.trim())
  if (filters.minSalary.trim()) params.set('minSalary', filters.minSalary.trim())
  if (filters.maxSalary.trim()) params.set('maxSalary', filters.maxSalary.trim())
  params.set('sort', filters.sort)
  params.set('page', String(filters.page))
  params.set('pageSize', String(filters.pageSize))
  return request<SearchResult>(`/api/jobs/search?${params.toString()}`)
}

export function getJob(id: string): Promise<Job> {
  return request<Job>(`/api/jobs/${id}`)
}

export function createJob(input: JobInput): Promise<Job> {
  return request<Job>('/api/jobs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
}

export function updateJob(id: string, input: JobInput): Promise<Job> {
  return request<Job>(`/api/jobs/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
}

export function getStats(): Promise<JobsStats> {
  return request<JobsStats>('/api/jobs/stats')
}
