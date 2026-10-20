// Cliente HTTP da API. A URL base pode ser sobrescrita em build com VITE_API_URL.
export const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:8080'

// TODO: implemente as chamadas do contrato de API (ver README na raiz, seção "CONTRATO DE API"):
//   POST /api/jobs
//   PUT  /api/jobs/{id}
//   GET  /api/jobs/{id}
//   GET  /api/jobs/search
//   GET  /api/jobs/stats
