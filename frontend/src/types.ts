// Tipos espelhando o contrato de API (README seção 6).

export type Seniority = 'junior' | 'pleno' | 'senior'
export type WorkMode = 'remoto' | 'hibrido' | 'presencial'
export type JobStatus = 'aberta' | 'encerrada'
export type SortOption = 'relevance' | 'newest' | 'salary_desc'

export interface Job {
  id: string
  title: string
  company: string
  description: string
  technologies: string[]
  seniority: Seniority
  workMode: WorkMode
  city: string | null
  salaryMin: number | null
  salaryMax: number | null
  status: JobStatus
  publishedAt: string
  updatedAt: string
}

export interface JobInput {
  title: string
  company: string
  description: string
  technologies: string[]
  seniority: Seniority
  workMode: WorkMode
  city: string | null
  salaryMin: number | null
  salaryMax: number | null
  status: JobStatus
}

export interface SearchItem {
  id: string
  title: string
  company: string
  seniority: Seniority
  workMode: WorkMode
  city: string | null
  technologies: string[]
  salaryMin: number | null
  salaryMax: number | null
  publishedAt: string
}

export interface SearchResult {
  page: number
  pageSize: number
  total: number
  items: SearchItem[]
}

export interface TechnologyCount {
  name: string
  count: number
}

export interface JobsStats {
  totalOpenJobs: number
  bySeniority: Record<string, number>
  byWorkMode: Record<string, number>
  topTechnologies: TechnologyCount[]
}

export interface SearchFilters {
  q: string
  seniority: string
  workMode: string
  technologies: string
  minSalary: string
  maxSalary: string
  sort: SortOption
  page: number
  pageSize: number
}

export const emptyFilters: SearchFilters = {
  q: '',
  seniority: '',
  workMode: '',
  technologies: '',
  minSalary: '',
  maxSalary: '',
  sort: 'relevance',
  page: 1,
  pageSize: 10,
}

export function formatSalary(min: number | null, max: number | null): string {
  if (min === null && max === null) return 'Salário não informado'
  // Centavos aparecem quando existem — a precisão salarial (P0.4) não pode sumir na exibição.
  const fmt = (v: number) => v.toLocaleString('pt-BR', {
    style: 'currency', currency: 'BRL',
    minimumFractionDigits: Number.isInteger(v) ? 0 : 2,
    maximumFractionDigits: 2,
  })
  if (min !== null && max !== null) return `${fmt(min)} – ${fmt(max)}`
  return min !== null ? `a partir de ${fmt(min)}` : `até ${fmt(max!)}`
}

export function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString('pt-BR', { day: '2-digit', month: 'short', year: 'numeric' })
}
