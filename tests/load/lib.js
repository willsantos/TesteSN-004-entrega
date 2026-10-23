// ============================================================================
// lib.js — helpers compartilhados dos cenários de carga k6 do TechJobs.
//
// Todos os cenários leem BASE_URL do ambiente (--env BASE_URL=...), com fallback
// para a porta oficial do contrato (8080). Em máquinas com o override local do
// compose (docker-compose.override.yml), use BASE_URL=http://localhost:18080.
//
// Os helpers /admin/* falham EXPLICITAMENTE (k6 fail) quando a resposta não é
// 2xx — setup com estado errado não deve produzir medição sem valor.
// ============================================================================
import http from 'k6/http';
import { fail } from 'k6';

export const BASE = __ENV.BASE_URL || 'http://localhost:8080';

/** Monta `${BASE}/api/jobs/search?...` a partir de um objeto de parâmetros. */
export function searchUrl(params) {
  const parts = Object.entries(params || {})
    .filter(([, v]) => v !== undefined && v !== null && `${v}` !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`);
  return `${BASE}/api/jobs/search${parts.length ? `?${parts.join('&')}` : ''}`;
}

/** GET em endpoint de administração; falha explícita se status != 2xx. */
export function adminGet(path) {
  const res = http.get(`${BASE}${path}`, { tags: { name: 'admin' } });
  if (res.status < 200 || res.status > 299) {
    fail(`adminGet ${path} falhou: HTTP ${res.status} — ${String(res.body || '').slice(0, 300)}`);
  }
  return res.json();
}

/** POST em endpoint de administração (ou da API); falha explícita se status != 2xx. */
export function adminPost(path, body) {
  const res = http.post(
    `${BASE}${path}`,
    body === undefined ? null : JSON.stringify(body),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'admin' } },
  );
  if (res.status < 200 || res.status > 299) {
    fail(`adminPost ${path} falhou: HTTP ${res.status} — ${String(res.body || '').slice(0, 300)}`);
  }
  return res.json();
}

export const drain = () => adminPost('/admin/indexing/drain');
export const setFault = (target, enabled) => adminPost('/admin/faults', { target, enabled });
export const reset = () => adminPost('/admin/reset');
export const seed = (count, s) => adminPost('/admin/seed', { count, seed: s });
export const cacheStats = () => adminGet('/admin/cache-stats');

/**
 * Busca uma página aleatória (pageSize=50) e sorteia um `items[].id` real —
 * útil para GETs de detalhe sobre ids que existem na base semeada.
 */
export function randomJobId(pages) {
  const total = Math.max(1, pages || 1);
  const page = Math.floor(Math.random() * total) + 1;
  const res = http.get(`${BASE}/api/jobs/search?pageSize=50&page=${page}`, {
    tags: { name: 'helper-random-id' },
  });
  if (res.status !== 200) {
    fail(`randomJobId: busca auxiliar respondeu HTTP ${res.status} (stack semeada e saudável?)`);
  }
  const items = res.json().items || [];
  if (items.length === 0) {
    fail(`randomJobId: página ${page} veio sem items (seed executado no setup?)`);
  }
  return items[Math.floor(Math.random() * items.length)].id;
}

/**
 * Todos os ids de vagas abertas, paginando a busca (pageSize=50). Usado no
 * setup de cenários que precisam de um estoque de ids reais (ex.: detail-cache).
 */
export function allJobIds() {
  const ids = [];
  for (let page = 1; page <= 500; page += 1) {
    const res = http.get(`${BASE}/api/jobs/search?pageSize=50&page=${page}`, {
      tags: { name: 'helper-list-ids' },
    });
    if (res.status !== 200) {
      fail(`allJobIds: HTTP ${res.status} na página ${page}`);
    }
    const body = res.json();
    const items = body.items || [];
    for (const item of items) ids.push(item.id);
    if (items.length === 0 || ids.length >= body.total) return ids;
  }
  fail('allJobIds: limite de 500 páginas excedido');
  return ids; // inalcançável (fail aborta); satisfaz o leitor
}

/**
 * Resumo de texto padrão dos cenários (usado no handleSummary). Impresso sem
 * depender de rede: os deltas de cache (hit/miss/error) NÃO podem ser lidos
 * dentro do handleSummary — cada cenário loga os stats iniciais no setup e a
 * comparação antes x depois é manual via
 * `curl "$BASE_URL/admin/cache-stats"` (ver docs/performance.md).
 */
export function renderSummary(data) {
  const lines = [];
  for (const name of Object.keys(data.metrics).sort()) {
    const m = data.metrics[name];
    let values;
    if (m.type === 'trend') {
      const v = m.values;
      values = `avg=${fmt(v.avg)} min=${fmt(v.min)} med=${fmt(v.med)} max=${fmt(v.max)} p(90)=${fmt(v['p(90)'])} p(95)=${fmt(v['p(95)'])} p(99)=${fmt(v['p(99)'])}`;
    } else if (m.type === 'rate') {
      values = `rate=${(m.values.rate * 100).toFixed(2)}%`;
    } else if (m.type === 'counter') {
      values = `count=${m.values.count}`;
    } else {
      values = `value=${fmt(m.values.value)}`;
    }
    lines.push(`${name.padEnd(42)} ${values}`);
    for (const th of m.thresholds || []) {
      lines.push(`  threshold ${th.ok ? 'PASS' : 'FAIL'}: ${th.threshold}`);
    }
  }
  return lines.join('\n');
}

function fmt(value) {
  if (value === undefined || value === null || Number.isNaN(value)) return '-';
  return `${value.toFixed(2)}${value >= 1000 ? ` (= ${(value / 1000).toFixed(1)}k)` : ''}`;
}

/** Fábrica do handleSummary padrão: cabeçalho do cenário + renderSummary. */
export function makeSummary(headerLines) {
  const header = Array.isArray(headerLines) ? headerLines : [headerLines];
  return function (data) {
    const out = [
      '='.repeat(78),
      ...header,
      '-'.repeat(78),
      'Contadores de cache: compare `curl "$BASE_URL/admin/cache-stats"` antes x depois',
      'do teste (os stats iniciais capturados no setup estão no log de início do teste).',
      'Rajada na MESMA chave canônica deve coalescer: delta de miss == 1 (single-flight).',
      '='.repeat(78),
      renderSummary(data),
    ];
    return { stdout: `${out.join('\n')}\n` };
  };
}
