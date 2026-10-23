// ============================================================================
// detail-cache.js — cache de detalhe (cache-aside): leitura FRIA x QUENTE.
//
// Objetivo .....: medir o custo do miss (GET /api/jobs/{id} servido do
//                 PostgreSQL + gravação no Redis) contra o custo do hit
//                 (servido do Redis), sob o mesmo volume de VUs.
// O que prova ..: que o cache-aside mantém p95 de leitura estável quando a
//                 chave está quente e que a leitura fria não degrada a API
//                 (nenhum 5xx), além de exercitar a população por chave.
// Como rodar ...: tests/load/run.sh detail-cache
//                 ou: k6 run -e BASE_URL=http://localhost:8080 detail-cache.js
// Variáveis ....: BASE_URL (default http://localhost:8080)
//                 SEED_COUNT (default 2000), VUS (default 20),
//                 DURATION_SECONDS (default 30, aplicado a CADA fase).
//
// Fases: cold (ids SEMPRE distintos dentro da janela do TTL -> cada leitura é
// miss -> PostgreSQL) e warm (cada VU lê o MESMO id -> hit de Redis após a
// primeira leitura). A pureza do cold vale enquanto o total de leituras frias
// for <= SEED_COUNT: se sua máquina gerar mais requisições que isso em uma
// janela, ids passam a se repetir (hit antes da hora) — aumente SEED_COUNT.
//
// Deltas de hit/miss/error: lidos MANUALMENTE via
// `curl "$BASE_URL/admin/cache-stats"` antes x depois (o setup loga os stats
// iniciais; o handleSummary do k6 não pode fazer chamadas HTTP).
// Esperado: details.miss cresce ~1 por leitura fria; details.hit cresce no warm.
// ============================================================================
import http from 'k6/http';
import { check, fail } from 'k6';
import { BASE, seed, reset, drain, cacheStats, allJobIds, makeSummary } from './lib.js';

const VUS = Number(__ENV.VUS || 20);
const DURATION_S = Number(__ENV.DURATION_SECONDS || 30);
const SEED_COUNT = Number(__ENV.SEED_COUNT || 2000);

export const options = {
  scenarios: {
    cold: {
      executor: 'constant-vus',
      vus: VUS,
      duration: `${DURATION_S}s`,
      exec: 'cold',
      startTime: '0s',
    },
    warm: {
      executor: 'constant-vus',
      vus: VUS,
      duration: `${DURATION_S}s`,
      exec: 'warm',
      startTime: `${DURATION_S}s`, // cold termina quando warm começa
    },
  },
  thresholds: {
    // Carga saudável: praticamente nenhuma falha HTTP.
    'http_req_failed': ['rate<0.01'],
    // Cold = PostgreSQL; 500ms é teto de sanidade local (ordem de grandeza).
    'http_req_duration{name:detail-cold}': ['p(95)<500'],
    // Warm = hit de Redis local; 50ms já seria degradado.
    'http_req_duration{name:detail-warm}': ['p(95)<50'],
    'checks': ['rate>0.99'],
  },
};

export function setup() {
  reset();
  seed(SEED_COUNT, 42);
  drain();
  const stats = cacheStats();
  console.log(`[detail-cache] stats iniciais: ${JSON.stringify(stats)}`);
  const ids = allJobIds();
  if (ids.length < VUS) {
    fail(`detail-cache: só ${ids.length} ids encontrados para ${VUS} VUs — verifique o seed.`);
  }
  console.log(
    `[detail-cache] ${ids.length} ids disponíveis; TTL detalhe = ${stats.config.detailsTtlSeconds}s; ` +
    `AVISO: pureza do cold garantida até ~${ids.length} leituras frias.`,
  );
  return { ids, vus: VUS };
}

// Fase fria: índice sem repetição entre VUs/iterações até esgotar o estoque
// (iter j do VU v -> j*VUS + (v-1)); acima disso recicla (documentado acima).
export function cold(data) {
  const idx = (__ITER * data.vus + (__VU - 1)) % data.ids.length;
  const res = http.get(`${BASE}/api/jobs/${data.ids[idx]}`, { tags: { name: 'detail-cold' } });
  check(res, { 'cold -> 200': (r) => r.status === 200 });
}

// Fase quente: cada VU fixa um id próprio (chaves já povoadas pela fase cold).
export function warm(data) {
  const id = data.ids[(__VU - 1) % data.ids.length];
  const res = http.get(`${BASE}/api/jobs/${id}`, { tags: { name: 'detail-warm' } });
  check(res, { 'warm -> 200': (r) => r.status === 200 });
}

export const handleSummary = makeSummary([
  'detail-cache — leitura fria (PostgreSQL) x quente (Redis), via cache-aside',
  'Esperado: p95(detail-cold) >> p95(detail-warm); nenhuma falha HTTP; nenhum 5xx.',
  'Deltas manuais: details.miss sobe ~1 por leitura fria; details.hit sobe no warm.',
]);
