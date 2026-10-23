// Parser puro de entrada de salário (usado no formulário de vagas).

export type SalaryParse = { ok: true; value: number | null } | { ok: false; error: string }

const SALARY_ERROR = 'Informe um valor numérico não negativo (ex.: 6500 ou 6500,50)'

/**
 * Converte a entrada livre de salário em número (ou null quando vazio).
 * Aceita "1234", "1234.5" e "1234,56" (vírgula vira ponto).
 */
export function parseSalaryInput(raw: string): SalaryParse {
  const trimmed = raw.trim()
  if (trimmed === '') return { ok: true, value: null }
  const normalized = trimmed.replace(',', '.')
  if (!/^\d+(\.\d+)?$/.test(normalized)) {
    return { ok: false, error: SALARY_ERROR }
  }
  return { ok: true, value: Number(normalized) }
}
