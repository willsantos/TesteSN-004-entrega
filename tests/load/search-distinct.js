// ============================================================================
// search-distinct.js — chaves de cache DISTINTAS a cada iteração (stampede
// potencial sem single-flight).
//
// Objetivo .....: gerar combinações q × filtros × página sempre novas, de
//                 modo que praticamente cada requisição seja um miss de cache
//                 e caia no Elasticsearch.
// O que prova ..: o comportamento sob pressão da FONTE de busca: p95 da rota
//                 quando o cache não ajuda (o pior caso real da leitura). O
//                 single-flight coalesce eventuais colisões de chave entre
//                 VUs, então a API segue estável (sem 5xx) mesmo com misses
//                 constantes.
// Como rodar ...: tests/load/run.sh search-distinct
//                 ou: k6 run -e BASE_URL=http://localhost:8080 search-distinct.js
// Variáveis ....: BASE_URL, SEED_COUNT (default 5000), VUS (default 10),
//                 DURATION_SECONDS (default 30).
//
// Comparação com search-canonical.js: mesmo executor/volume, mas aqui cada
// iteração constrói uma chave inédita (q rotacionado em termos do seed,
// seniority/workMode/technologies/página variando com um contador global
// __VU*1.000.003+__ITER — 17.280 combinações antes de reciclar). A diferença
// de p95 entre os dois cenários é o valor do cache de busca.
//
// Deltas manuais via `curl "$BASE_URL/admin/cache-stats"`: search.miss cresce
// ~1 por requisição (cada chave nova); hits raros (só colisões acidentais).
// ============================================================================
import http from 'k6/http';
import { check } from 'k6';
import { seed, reset, drain, cacheStats, searchUrl, makeSummary } from './lib.js';

const VUS = Number(__ENV.VUS || 10);
const DURATION_S = Number(__ENV.DURATION_SECONDS || 30);
const SEED_COUNT = Number(__ENV.SEED_COUNT || 5000);

// Termos que casam com o seed determinístico (papéis/títulos gerados pelo seed 42).
const TERMS = ['desenvolvedor', 'engenheiro', 'analista', 'dados', 'software', 'plataforma'];
const SENIORITIES = ['junior', 'pleno', 'senior'];
const WORK_MODES = ['remoto', 'hibrido', 'presencial'];
const TECHS = ['python', 'react', 'docker', 'kubernetes', 'postgresql', 'redis', 'aws', 'typescript'];

export const options = {
  scenarios: {
    distinct: {
      executor: 'constant-vus',
      vus: VUS,
      duration: `${DURATION_S}s`,
    },
  },
  thresholds: {
    'http_req_failed': ['rate<0.01'],
    // Todo request é uma consulta nova no Elasticsearch: 500ms é o teto de
    // sanidade local; compare com o p95 do search-canonical (hits).
    'http_req_duration{name:search-distinct}': ['p(95)<500'],
    'checks': ['rate>0.99'],
  },
};

export function setup() {
  reset();
  seed(SEED_COUNT, 42);
  drain();
  const stats = cacheStats();
  console.log(`[search-distinct] stats iniciais: ${JSON.stringify(stats)}`);
  return {};
}

export default function () {
  // Contador "global" por (VU, iteração): combinação distinta de chave a cada
  // requisição — misses constantes na fonte (Elasticsearch).
  const i = __VU * 1000003 + __ITER;
  const params = {
    q: TERMS[i % TERMS.length],
    seniority: SENIORITIES[Math.floor(i / TERMS.length) % SENIORITIES.length],
    workMode: WORK_MODES[Math.floor(i / (TERMS.length * SENIORITIES.length)) % WORK_MODES.length],
    technologies: TECHS[Math.floor(i / (TERMS.length * SENIORITIES.length * WORK_MODES.length)) % TECHS.length],
    page: String((Math.floor(i / (TERMS.length * SENIORITIES.length * WORK_MODES.length * TECHS.length)) % 40) + 1),
    pageSize: '10',
    sort: 'relevance',
  };
  const res = http.get(searchUrl(params), { tags: { name: 'search-distinct' } });
  check(res, { 'search -> 200': (r) => r.status === 200 });
}

export const handleSummary = makeSummary([
  'search-distinct — chave de cache nova a cada requisição (fonte sob pressão)',
  'Esperado: p95 maior que search-canonical no mesmo volume; nenhum 5xx (single-flight coalesce).',
  'Deltas manuais: search.miss cresce ~1 por requisição; hits raros.',
]);
