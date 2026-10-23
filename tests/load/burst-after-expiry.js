// ============================================================================
// burst-after-expiry.js — rajada sobre a MESMA consulta logo após o TTL expirar
// (prova do single-flight/coalescing de misses).
//
// Objetivo .....: popular uma chave de busca, esperar EXATAMENTE o TTL da chave
//                 expirar e disparar uma rajada (30 VUs, 30 iterações
//                 compartilhadas) sobre a MESMA query no mesmo instante.
// O que prova ..: sem preenchimento coalescido, a rajada geraria ~30 misses
//                 simultâneos no Elasticsearch (stampede). Com single-flight,
//                 exatamente 1 requisição (líder) consulta a fonte e as demais
//                 reutilizam o resultado. O script garante latências/thresholds;
//                 a contagem exata de miss/hit é confirmada manualmente:
//                 delta esperado em /admin/cache-stats ao final do teste —
//                 search.miss == 2 (1 do prime + 1 do líder da rajada) e
//                 search.hit == 29 (seguidores revalidam o cache e contam hit).
// Como rodar ...: tests/load/run.sh burst-after-expiry
//                 ou: k6 run -e BASE_URL=http://localhost:8080 burst-after-expiry.js
// Variáveis ....: BASE_URL, SEED_COUNT (default 1000), BURST_VUS (default 30).
//
// Nota de implementação: as opções de execução do k6 são congeladas no init,
// ANTES do setup — por isso o startTime da rajada não é "calculado" a partir do
// TTL lido da API. Equivalente: os 30 VUs da rajada iniciam junto com o prime e
// dormem `TTL + 2` segundos (TTL lido de /admin/cache-stats no setup); todos
// acordam no mesmo instante, já com a chave expirada, e disparam a rajada.
//
// Duração total: ~TTL + 3s (com o default de busca, 60s -> ~63s).
// ============================================================================
import http from 'k6/http';
import { check, sleep, fail } from 'k6';
import { seed, reset, drain, cacheStats, searchUrl, makeSummary } from './lib.js';

const SEED_COUNT = Number(__ENV.SEED_COUNT || 1000);
const BURST_VUS = Number(__ENV.BURST_VUS || 30);

// Consulta com centenas de resultados no seed 42 (bem representativa de carga).
const QUERY = { q: 'desenvolvedor', page: '1', pageSize: '10', sort: 'relevance' };

export const options = {
  scenarios: {
    // Fase 1: uma única requisição popula a chave em t ≈ 0.
    prime: {
      executor: 'shared-iterations',
      vus: 1,
      iterations: 1,
      exec: 'prime',
      startTime: '0s',
    },
    // Fase 2: 30 VUs × 1 iteração na MESMA query (aguardam o TTL expirar).
    burst: {
      executor: 'shared-iterations',
      vus: BURST_VUS,
      iterations: BURST_VUS, // cada VU executa exatamente 1 iteração
      exec: 'burst',
      startTime: '0s',
    },
  },
  thresholds: {
    'http_req_failed': ['rate<0.01'],
    'http_req_duration{name:burst-prime}': ['p(95)<500'],
    // Na rajada, 29/30 são hit/reuso do payload do líder; só o líder consulta o ES.
    'http_req_duration{name:burst-same-query}': ['p(95)<200'],
    'checks': ['rate>0.99'],
  },
};

export function setup() {
  reset();
  seed(SEED_COUNT, 42);
  drain();
  const stats = cacheStats();
  const ttl = stats.config.searchTtlSeconds;
  console.log(`[burst-after-expiry] stats iniciais: ${JSON.stringify(stats)}`);
  console.log(
    `[burst-after-expiry] TTL de busca = ${ttl}s -> rajada dispara em t ≈ ${ttl + 2}s. ` +
    'Depois do teste: `curl "$BASE_URL/admin/cache-stats"` — delta esperado: search.miss==2, search.hit==29.',
  );
  const res = http.get(searchUrl(QUERY), { tags: { name: 'setup-burst' } });
  if (res.status !== 200) {
    fail(`setup: consulta-base respondeu HTTP ${res.status}`);
  }
  return { ttl, expectedTotal: res.json().total };
}

// Fase 1: popula a chave (miss de cache -> ES -> grava com TTL da config).
export function prime(data) {
  const res = http.get(searchUrl(QUERY), { tags: { name: 'burst-prime' } });
  check(res, {
    'prime -> 200': (r) => r.status === 200,
    'prime total bate': (r) => r.json().total === data.expectedTotal,
  });
}

// Fase 2: dorme até o TTL expirar e dispara a rajada simultânea.
export function burst(data) {
  sleep(data.ttl + 2); // chave populada em t≈0 expira em t≈TTL; +2s de folga
  const res = http.get(searchUrl(QUERY), { tags: { name: 'burst-same-query' } });
  check(res, {
    'burst -> 200': (r) => r.status === 200,
    'burst total bate': (r) => r.json().total === data.expectedTotal,
  });
}

export const handleSummary = makeSummary([
  'burst-after-expiry — rajada de 30 VUs na mesma query após o TTL expirar',
  'Prova do single-flight: delta manual em /admin/cache-stats — search.miss==2, search.hit==29.',
  'Sem coalescing, a mesma rajada geraria ~30 misses no Elasticsearch (stampede).',
]);
