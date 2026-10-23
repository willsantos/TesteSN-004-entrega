import { describe, expect, it } from 'vitest'
import { parseSalaryInput } from './salary'

const ERROR_MSG = 'Informe um valor numérico não negativo (ex.: 6500 ou 6500,50)'

describe('parseSalaryInput', () => {
  it('entrada vazia (ou só espaços) vira null', () => {
    expect(parseSalaryInput('')).toEqual({ ok: true, value: null })
    expect(parseSalaryInput('   ')).toEqual({ ok: true, value: null })
  })

  it('inteiro válido', () => {
    expect(parseSalaryInput('6500')).toEqual({ ok: true, value: 6500 })
  })

  it('vírgula como separador decimal', () => {
    expect(parseSalaryInput('6500,50')).toEqual({ ok: true, value: 6500.5 })
  })

  it('ponto como separador decimal', () => {
    expect(parseSalaryInput('6500.50')).toEqual({ ok: true, value: 6500.5 })
  })

  it('faz trim antes de validar', () => {
    expect(parseSalaryInput('  6500  ')).toEqual({ ok: true, value: 6500 })
  })

  it.each(['abc', '-5', '1.2.3', '12,34,56'])('rejeita %s com mensagem', (raw) => {
    const result = parseSalaryInput(raw)
    expect(result).toEqual({ ok: false, error: ERROR_MSG })
  })
})
