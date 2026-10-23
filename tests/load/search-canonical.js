// ============================================================================
// search-canonical.js — consultas textualmente DIFERENTES, canonicamente IGUAIS.
//
// Objetivo .....: variar espaçamento/case do `q`, ordem/duplicatas/grafia das
//                 `technologies` e zeros à direita de `minSalary` sem sair da
//                 MESMA chave canônica de cache (sha256 da forma canônica).
// O que prova ..: que a normalização canônica (q trim/collapse/lowercase;
//                 tecnologias lowercase+dedup+ordenadas; decimais sem zeros
//                 à direita) faz variações de escrita reutilizarem UM único
//                 cache — 1 miss na primeira consulta e hits dali em diante —
//                 e que todas as variações devolvem o MESMO total (mesma
//                 consulta no Elasticsearch).
// Como rodar ...: tests/load/run.sh search-canonical
//                 ou: k6 run -e BASE_URL=http://localhost:8080 search-canonical.js
// Variáveis ....: BASE_URL, SEED_COUNT (default 2000), VUS (default 10),
//                 DURATION_SECONDS (default 30).
//
// Comparação com search-distinct.js: lá cada iteração usa uma chave nova
// (misses constantes no Elasticsearch); aqui a chave é uma só (quase 100% hits
// de Redis). Rodar os dois com o mesmo volume mostra o efeito do cache por
// chave canônica no p95.
//
// Deltas manuais via `curl "$BASE_URL/admin/cache-stats"`: search.miss deve
// subir pouquíssimo (misses só na primeira variação e após expirar o TTL de
// busca); search.hit deve dominar.
// ============================================================================
import http from 'k6/http';
import { check, fail } from 'k6';
import { seed, reset, drain, cacheStats, searchUrl, makeSummary } from './lib.js';

const VUS = Number(__ENV.VUS || 10);
const DURATION_S = Number(__ENV.DURATION_SECONDS || 30);
const SEED_COUNT = Number(__ENV.SEED_COUNT || 2000);

// Consulta-base com termos presentes no seed determinístico (seed 42).
const BASE_PARAMS = {
  q: 'desenvolvedor',
  seniority: 'pleno',
  workMode: 'remoto',
  sort: 'relevance',
  page: '1',
  pageSize: '10',
};

// As quatro variações colapsam para a MESMA chave canônica:
//  - q: espaços extras/ Bordas / CAIXA ALTA -> trim + collapse + lowercase;
//  - technologies: ordem trocada, duplicata, espaços em volta da vírgula
//    (semântica é E -> lowercase + dedup + ordenação alfabética);
//  - minSalary: 5000 == 5000.00 == 5000.000 (zeros à direita removidos).
const VARIATIONS = [
  { ...BASE_PARAMS, technologies: 'python,react', minSalary: '5000' },
  { ...BASE_PARAMS, q: '  DESENVOLVEDOR    ', technologies: 'REACT,Python', minSalary: '5000.00' },
  { ...BASE_PARAMS, q: 'desenvolvedor ', technologies: 'python,react,python', minSalary: '5000.000' },
  { ...BASE_PARAMS, q: ' Desenvolvedor', technologies: ' python , react ', minSalary: '5000.0' },
];

export const options = {
  scenarios: {
    canonical: {
      executor: 'constant-vus',
      vus: VUS,
      duration: `${DURATION_S}s`,
    },
  },
  thresholds: {
    'http_req_failed': ['rate<0.01'],
    // Quase tudo deve ser hit de Redis: teto de sanidade local.
    'http_req_duration{name:search-canonical}': ['p(95)<100'],
    'checks': ['rate>0.99'],
  },
};

export function setup() {
  reset();
  seed(SEED_COUNT, 42);
  drain();
  const stats = cacheStats();
  console.log(`[search-canonical] stats iniciais: ${JSON.stringify(stats)}`);
  // Total esperado da consulta canônica (todas as variações têm que bater).
  const res = http.get(searchUrl(VARIATIONS[0]), { tags: { name: 'setup-canonical' } });
  if (res.status !== 200) {
    fail(`setup: busca da consulta-base respondeu HTTP ${res.status}`);
  }
  const expectedTotal = res.json().total;
  console.log(`[search-canonical] total esperado da consulta canônica: ${expectedTotal}`);
  return { expectedTotal };
}

export default function (data) {
  const params = VARIATIONS[__ITER % VARIATIONS.length];
  const res = http.get(searchUrl(params), { tags: { name: 'search-canonical' } });
  check(res, {
    'search -> 200': (r) => r.status === 200,
    // Prova de equivalência semântica: a chave é igual, logo o total também.
    'total idêntico entre variações': (r) => r.json().total === data.expectedTotal,
  });
}

export const handleSummary = makeSummary([
  'search-canonical — variações de escrita da MESMA consulta (mesma chave canônica)',
  'Esperado: 1 miss (primeira variação) e hits no restante; total igual em 100% das respostas.',
  'Deltas manuais: search.hit domina; search.miss praticamente parado durante o teste.',
]);
