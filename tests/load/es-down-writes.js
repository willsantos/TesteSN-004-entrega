// ============================================================================
// es-down-writes.js — escrita preservada com o Elasticsearch FORA + buscas em
// 503 CONTROLADO; recuperação validada manualmente após o teardown.
//
// Objetivo .....: com a falha simulada do Elasticsearch ligada, exercitar ao
//                 mesmo tempo (a) POSTs e PUTs — que precisam continuar
//                 respondendo 2xx, pois o PostgreSQL é a fonte da verdade e o
//                 outbox guarda a indexação pendente — e (b) buscas, que
//                 DEVEM responder 503 Problem Details (falha ESPERADA).
// O que prova ..: que a indisponibilidade da projeção de busca não bloqueia a
//                 escrita oficial (nada se perde: os eventos ficam pendentes no
//                 outbox e são aplicados no drain) e que a falha da busca é
//                 um contrato controlado, não um erro genérico.
// Como rodar ...: tests/load/run.sh es-down-writes   (isolado dos outros cenários)
//                 ou: k6 run -e BASE_URL=http://localhost:8080 es-down-writes.js
// Variáveis ....: BASE_URL, VUS (default 5 — limita o backlog de eventos
//                 pendentes no outbox), DURATION_SECONDS (default 30).
//
// Nota sobre o 503 esperado: no k6 v1.x a tag `expected_response` é recalculada
// pela responseCallback e SOBRESCREVE qualquer valor setado manualmente em
// tags (verificado no código-fonte do k6 v1.0.0). O mecanismo efetivo para não
// contar o 503 como falha em http_req_failed é trocar o callback:
// http.setResponseCallback(http.expectedStatuses(200, 201, 204, 503)) — 2xx e
// 503 são "esperados"; qualquer OUTRO status (400/500/...) conta como falha.
//
// Recuperação (pós-teardown): o teardown desliga a falha e roda drain, mas o
// k6 não permite validar respostas depois disso. A validação manual — busca
// voltando a 200 com as vagas criadas DURANTE a falha visíveis — está em
// docs/performance.md e docs/demo.md (passos 8-9).
// ============================================================================
import http from 'k6/http';
import { check, fail } from 'k6';
import { BASE, seed, drain, setFault, cacheStats, searchUrl, makeSummary } from './lib.js';

const VUS = Number(__ENV.VUS || 5);
const DURATION_S = Number(__ENV.DURATION_SECONDS || 30);

export const options = {
  scenarios: {
    writes: {
      executor: 'constant-vus',
      vus: VUS,
      duration: `${DURATION_S}s`,
    },
  },
  thresholds: {
    // Escritas (2xx) + buscas (503) são todas "esperadas": nenhuma falha real.
    'http_req_failed': ['rate<0.01'],
    'checks': ['rate>0.99'],
    // Escrita confirma no PostgreSQL mesmo com a projeção fora.
    'http_req_duration{name:esd-post}': ['p(95)<500'],
    'http_req_duration{name:esd-put}': ['p(95)<500'],
    // O 503 deve ser rápido: a falha é detectada antes de chamar o ES.
    'http_req_duration{name:esd-search-503}': ['p(95)<250'],
  },
};

export function setup() {
  seed(1000, 42);
  drain();
  const stats = cacheStats();
  console.log(`[es-down-writes] stats iniciais: ${JSON.stringify(stats)}`);

  // Vaga-alvo criada com a stack saudável (POST -> 201; drain indexa).
  const created = http.post(
    `${BASE}/api/jobs`,
    JSON.stringify(jobPayload('Vaga alvo es-down', 15000)),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'setup-esd' } },
  );
  if (created.status !== 201) {
    fail(`es-down-writes: POST da vaga-alvo respondeu HTTP ${created.status} — ${created.body}`);
  }
  drain();
  setFault('elasticsearch', true);
  console.log('[es-down-writes] falha do Elasticsearch LIGADA — teardown desliga e faz drain.');
  return { targetId: created.json().id };
}

function jobPayload(title, salaryMax) {
  return {
    title,
    company: 'Demo Load',
    description:
      'Vaga criada pelo cenário de carga es-down-writes para provar que a escrita oficial ' +
      'em PostgreSQL continua confirmada com a projeção de busca indisponível.',
    technologies: ['Docker', 'PostgreSQL'],
    seniority: 'senior',
    workMode: 'remoto',
    city: null,
    salaryMin: Math.round(salaryMax * 0.8 * 100) / 100,
    salaryMax,
    status: 'aberta',
  };
}

export default function (data) {
  // 503 passa a ser status "esperado": não conta em http_req_failed; qualquer
  // outro status fora de {200,201,204,503} conta como falha de verdade.
  http.setResponseCallback(http.expectedStatuses(200, 201, 204, 503));

  // (a) POST de vaga nova -> 201 confirmado no PostgreSQL (outbox pendente).
  let res = http.post(
    `${BASE}/api/jobs`,
    JSON.stringify(jobPayload(`Vaga k6 es-down ${__VU}-${__ITER}`, 18000)),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'esd-post' } },
  );
  check(res, { 'POST 201 com ES fora': (r) => r.status === 201 });

  // (b) PUT na vaga-alvo -> 200; cada iteração muda o teto salarial (payload
  //     válido: max >= min), gerando evento de indexação pendente no outbox.
  const payload = jobPayload('Vaga alvo es-down', 15000 + (__ITER % 5) * 250);
  res = http.put(
    `${BASE}/api/jobs/${data.targetId}`,
    JSON.stringify(payload),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'esd-put' } },
  );
  check(res, { 'PUT 200 com ES fora': (r) => r.status === 200 });

  // (c) Busca -> 503 Problem Details (falha ESPERADA e checada explicitamente).
  res = http.get(searchUrl({ q: 'desenvolvedor', pageSize: '10' }), {
    tags: { name: 'esd-search-503' },
  });
  check(res, {
    'busca 503 controlada': (r) => r.status === 503,
    '503 em Problem Details': (r) =>
      `${r.headers['Content-Type'] || ''}${r.headers['content-type'] || ''}`.includes('application/problem+json'),
  });
}

export function teardown() {
  setFault('elasticsearch', false);
  drain(); // aplica o backlog do outbox e invalida o cache de busca
  console.log(
    '[es-down-writes] falha do Elasticsearch DESLIGADA e drain executado no teardown. ' +
    'Validação manual da recuperação: `curl "$BASE_URL/api/jobs/search?q=vaga+k6"` deve ' +
    'responder 200 e listar as vagas criadas durante a falha (ver docs/demo.md, passos 8-9).',
  );
}

export const handleSummary = makeSummary([
  'es-down-writes — escrita preservada com Elasticsearch fora; busca em 503 controlado',
  'Esperado: POST/PUT 100% em 2xx; buscas 100% em 503 (não contam como falha HTTP).',
  'Deltas manuais: search.miss cresce; após drain, buscas voltam a 200 com as vagas novas.',
]);
