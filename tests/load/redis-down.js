// ============================================================================
// redis-down.js — degradação com o Redis FORA: nenhuma leitura vira 5xx.
//
// Objetivo .....: manter tráfego misto (detalhe + busca + PUT de ronda) com a
//                 falha simulada do Redis ligada (POST /admin/faults).
// O que prova ..: que a falha do cache NÃO derruba a API — o detalhe é servido
//                 do PostgreSQL, a busca vai direta ao Elasticsearch e a
//                 escrita segue normal. O efeito colateral visível é o contador
//                 `error` crescendo em /admin/cache-stats (leitura MANUAL,
//                 deltas antes x depois — os stats iniciais estão no log do
//                 setup), não um aumento de erros HTTP.
// Como rodar ...: tests/load/run.sh redis-down      (isolado dos outros cenários)
//                 ou: k6 run -e BASE_URL=http://localhost:8080 redis-down.js
// Variáveis ....: BASE_URL, VUS (default 10), DURATION_SECONDS (default 30).
//
// AVISO: liga falha simulada DEV-ONLY; o teardown DESLIGA. Se o processo for
// morto antes do fim, desligue manualmente:
//   curl -X POST "$BASE_URL/admin/faults" -H 'Content-Type: application/json' \
//        -d '{"target":"redis","enabled":false}'
// ============================================================================
import http from 'k6/http';
import { check, fail } from 'k6';
import { BASE, seed, drain, setFault, cacheStats, allJobIds, searchUrl, makeSummary } from './lib.js';

const VUS = Number(__ENV.VUS || 10);
const DURATION_S = Number(__ENV.DURATION_SECONDS || 30);

export const options = {
  scenarios: {
    mixed: {
      executor: 'constant-vus',
      vus: VUS,
      duration: `${DURATION_S}s`,
    },
  },
  thresholds: {
    // A falha do Redis NÃO vira 5xx: este é o threshold central do cenário.
    'http_req_failed': ['rate<0.01'],
    // Sem cache, o detalhe vem do PostgreSQL e a busca direto do Elasticsearch.
    'http_req_duration{name:rd-detail}': ['p(95)<500'],
    'http_req_duration{name:rd-search}': ['p(95)<500'],
    // Escrita não depende do cache (só da invalidação, que tolera falha).
    'http_req_duration{name:rd-put}': ['p(95)<500'],
    'checks': ['rate>0.99'],
  },
};

export function setup() {
  seed(1000, 42);
  drain();
  const stats = cacheStats();
  console.log(`[redis-down] stats iniciais: ${JSON.stringify(stats)}`);
  const ids = allJobIds();
  if (ids.length === 0) {
    fail('redis-down: nenhum id encontrado após o seed.');
  }
  // Vaga-alvo do PUT de ronda: PUT idempotente (mesmo payload = mesmo estado,
  // updatedAt preservado) — exercita escrita + invalidação de detalhe sob falha.
  const detailRes = http.get(`${BASE}/api/jobs/${ids[0]}`, { tags: { name: 'setup-rd' } });
  if (detailRes.status !== 200) {
    fail(`redis-down: detalhe da vaga-alvo respondeu HTTP ${detailRes.status}`);
  }
  const job = detailRes.json();
  const putPayload = {
    title: job.title,
    company: job.company,
    description: job.description,
    technologies: job.technologies,
    seniority: job.seniority,
    workMode: job.workMode,
    city: job.city,
    salaryMin: job.salaryMin,
    salaryMax: job.salaryMax,
    status: job.status,
  };
  setFault('redis', true);
  console.log('[redis-down] falha do Redis LIGADA — teardown desliga ao final.');
  return { ids, targetId: job.id, putPayload };
}

export default function (data) {
  // 1) Detalhe: cache fora -> PostgreSQL (resposta 200, `error++` nos stats).
  const id = data.ids[(__ITER + __VU) % data.ids.length];
  let res = http.get(`${BASE}/api/jobs/${id}`, { tags: { name: 'rd-detail' } });
  check(res, { 'detalhe 200 sem Redis': (r) => r.status === 200 });

  // 2) Busca: cache de busca fora -> Elasticsearch direto (200, `error++`).
  res = http.get(
    searchUrl({ q: 'desenvolvedor', page: String((__ITER % 5) + 1), pageSize: '10' }),
    { tags: { name: 'rd-search' } },
  );
  check(res, { 'busca 200 sem Redis': (r) => r.status === 200 });

  // 3) PUT leve de ronda (idempotente): escrita confirma no PostgreSQL.
  res = http.put(
    `${BASE}/api/jobs/${data.targetId}`,
    JSON.stringify(data.putPayload),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'rd-put' } },
  );
  check(res, { 'PUT 200 sem Redis': (r) => r.status === 200 });
}

export function teardown() {
  setFault('redis', false);
  console.log('[redis-down] falha do Redis DESLIGADA no teardown.');
}

export const handleSummary = makeSummary([
  'redis-down — tráfego misto com o Redis fora (nenhum 5xx esperado)',
  'Deltas manuais: details.error e search.error crescem em /admin/cache-stats.',
  'Falha de cache é contada e contornada — nunca propagada como erro HTTP.',
]);
