import { describe, expect, it } from 'vitest'
import { emptyFilters } from './types'

describe('emptyFilters', () => {
  it('começa ordenado por relevância, página 1', () => {
    expect(emptyFilters.sort).toBe('relevance')
    expect(emptyFilters.page).toBe(1)
    expect(emptyFilters.pageSize).toBe(10)
  })

  it('começa sem nenhum filtro de texto preenchido', () => {
    expect(emptyFilters.q).toBe('')
    expect(emptyFilters.seniority).toBe('')
    expect(emptyFilters.workMode).toBe('')
    expect(emptyFilters.technologies).toBe('')
    expect(emptyFilters.minSalary).toBe('')
    expect(emptyFilters.maxSalary).toBe('')
  })
})
