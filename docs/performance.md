# Performance — cenários de carga (k6), metodologia e resultados

Este documento descreve a suíte de carga do TechJobs (`tests/load/`): o que cada
cenário mede, como rodar e como interpretar os contadores de cache. Os números
preenchidos na tabela são **medidas locais** — servem como ordem de grandeza e
prova de ausência de erro, **não** como benchmark oficial (ver nota honesta no fim).

## Objetivo

Tornar as propriedades de performance do sistema **reproduzíveis**: cada cenário é
um script k6 versionado, com thresholds explícitos que separam carga saudável de
falha esperada. Eles existem para responder, com evidência:

1. O cache de leitura (detalhe e busca) mantém o p95 estável quando quente?
2. Consultas canonicamente equivalentes colapsam numa única chave (1 miss, resto hit)?
3. Uma rajada sobre a mesma chave logo após o TTL expirar coalesce num único miss
   (single-flight), em vez de ~N misses no Elasticsearch?
4. A falha do Redis não vira 5xx e a falha do Elasticsearch não bloqueia escrita?

## Como rodar

Pré-requisitos: **docker** (para a stack e, opcionalmente, para o k6 em container;
k6 nativo é usado se instalado). Stack descartável — os cenários usam `/admin/*`
(reset/seed/faults) e **substituem a base**.

```bash
# 1) Stack viva e saudável
docker compose up -d --build
curl -s localhost:8080/health            # {"status":"ok"}

# 2) Todos os cenários sem falha simulada (detail-cache, search-canonical,
#    search-distinct, burst-after-expiry):
tests/load/run.sh

# 3) Cenários com falha simulada — SEMPRE isolados, um por vez:
tests/load/run.sh redis-down
tests/load/run.sh es-down-writes
```

> **Porta local:** se sua máquina usa o override do compose
> (`docker-compose.override.yml`), a API responde em **18080** em vez de 8080.
> Rode com `BASE_URL=http://localhost:18080 tests/load/run.sh` (idem para os
> `curl` de cache-stats). Todos os scripts também aceitam
> `VUS`, `DURATION_SECONDS`, `SEED_COUNT` e `BURST_VUS`.

Cada cenário imprime um resumo com as métricas (`http_req_duration`,
`http_req_failed`, `checks`…) e o resultado PASS/FAIL de cada threshold.

## Metodologia (cenário → o que mede → thresholds)

| Cenário | Carga | O que mede | Prova | Thresholds principais |
|---|---|---|---|---|
| `detail-cache.js` | 2 fases de ~20 VUs × 30s: cold (ids sempre distintos → miss) e warm (mesmo id → hit) | p95 da leitura de detalhe fria (PostgreSQL) × quente (Redis) | cache-aside mantém leitura estável e fria não degrada | `http_req_failed < 1%`; p95 cold < 500ms; p95 warm < 50ms |
| `search-canonical.js` | ~10 VUs × 30s alternando 4 variações da mesma consulta (case/espaçamento, ordem/duplicata de technologies, zeros de minSalary) | p95 de busca com chave canônica única | variações de escrita reutilizam 1 cache (1 miss, resto hit) e o total é idêntico | `http_req_failed < 1%`; p95 < 100ms; total igual em 100% |
| `search-distinct.js` | ~10 VUs × 30s com chave nova por iteração (q/filtros/página variando) | p95 de busca com **todo** request caindo no Elasticsearch | pior caso real da leitura; compara com `search-canonical` no mesmo volume | `http_req_failed < 1%`; p95 < 500ms |
| `burst-after-expiry.js` | 1 GET popular a chave; espera `TTL+2s` (TTL lido de `/admin/cache-stats`); rajada de 30 VUs × 1 iteração na mesma query | coalescing de miss na rajada pós-expiração | single-flight: delta esperado `search.miss==2` (prime + líder) e `search.hit==29` | `http_req_failed < 1%`; p95 rajada < 200ms |
| `redis-down.js` | ~10 VUs × 30s (detalhe + busca + PUT idempotente) com falha do Redis ligada | ausência de 5xx sem cache | falha de cache é contornada (`error` cresce nos contadores, não nos status HTTP) | `http_req_failed < 1%`; p95 por rota < 500ms |
| `es-down-writes.js` | ~5 VUs × 30s: POST/PUT (2xx esperado) + busca (503 esperado) com falha do ES ligada | escrita preservada com a projeção fora | POST/PUT confirmam no PostgreSQL (outbox pendente); busca responde 503 Problem Details e não conta como falha | `http_req_failed < 1%` (503 marcado como esperado via `expectedStatuses`); p95 escrita < 500ms |

Os 4 primeiros cenários rodam em sequência em `run.sh` (sem falha simulada);
`redis-down` e `es-down-writes` ligam falhas via `/admin/faults` e desligam no
teardown — rode-os isolados.

## Tabela de resultados

Preencher uma linha por execução. Para o commit/ambiente:

```bash
git rev-parse --short HEAD   # execute antes de rodar os testes e anote na linha
```

| Cenário | Data | Commit/ambiente | VUs/duração | p50 (ms) | p95 (ms) | p99 (ms) | Throughput (req/s) | http_req_failed | Observações |
|---|---|---|---|---|---|---|---|---|---|
| detail-cache (cold) | 2026-10-22 | `dbe33bf` + working tree (P0.1–P1.1); Fedora fc43, Docker local, k6 1.0.0 (`--network host`) | 20 VUs × 30s (seed 2000) | 1,05 | 2,10 | — (a) | 15 244 (b) | 0,00% | 937 511 reqs no cenário; max 26,6 ms |
| detail-cache (warm) | 2026-10-22 | idem | 20 VUs × 30s | 1,00 | 2,01 | — (a) | (mesma execução) | 0,00% | max 30,9 ms; frio × quente no mesmo teto (fonte local é rápida) |
| search-canonical | 2026-10-22 | idem | 10 VUs × 30s (seed 2000) | 0,83 | 1,63 | — (a) | 8 504 | 0,00% | 259 164 reqs; total idêntico nas 4 variações; `search.miss` praticamente parado |
| search-distinct | 2026-10-22 | idem | 10 VUs × 30s (seed 5000) | 0,92 | 11,05 | — (a) | 3 282 | 0,00% | 101 146 reqs, chave nova por iteração — p95 ~7× o da canônica (toda consulta vai ao ES) |
| burst-after-expiry | 2026-10-22 | idem | prime + 30 VUs × 1 iteração (TTL 60s) | 15,12 | 16,91 | — (a) | 36 reqs (rajada) | 0,00% | **single-flight comprovado**: delta `search.miss==2` (prime + líder) e `search.hit==29`; sem coalescing seriam ~30 misses |
| redis-down | 2026-10-22 | idem | 10 VUs × 30s (seed 1000) | 7,15 | 17,77 | — (a) | 1 410 | 0,00% | **zero 5xx** com Redis fora; p95 por rota: detalhe 1,97 / busca 10,35 / PUT 25,55 ms; contadores: `details.error`=28 974, `search.error`=14 487 |
| es-down-writes | 2026-10-22 | idem | 5 VUs × 30s (seed 1000) | 2,63 | 11,79 | — (a) | 990 | 0,00% (c) | POST/PUT 2xx durante falha do ES (p95 POST 3,95 / PUT 14,69 ms); busca 503 Problem Details (p95 1,29 ms); pós-drain as 10 754 vagas da carga visíveis na busca |

(a) O summary padrão do k6 1.0.0 emite p50/p90/p95/max; p99 exigiria trend custom — deixado como exercício de leitura do `--summary-export`.
(b) Throughput agregado do cenário (fases cold+warm juntas).
(c) O 503 da busca é marcado como resposta esperada (`http.setResponseCallback(http.expectedStatuses(2xx, 503))`); qualquer outro status contaria como falha.

## Como ler os contadores de cache

Os contadores vêm de `GET /admin/cache-stats` (em memória, uma instância da API):

```json
{
  "config": { "detailsTtlSeconds": 300, "searchTtlSeconds": 60 },
  "details": { "hit": 0, "miss": 0, "error": 0 },
  "search":  { "hit": 0, "miss": 0, "error": 0 }
}
```

Protocolo para **cada** cenário: capture os contadores **antes** (o setup de cada
script também loga os stats iniciais), rode o cenário, capture **depois** e compare
os deltas:

```bash
# antes
curl -s "$BASE_URL/admin/cache-stats" | tee /tmp/stats-antes.json
# ... rodar o cenário ...
# depois
curl -s "$BASE_URL/admin/cache-stats" | tee /tmp/stats-depois.json
diff <(jq -S . /tmp/stats-antes.json) <(jq -S . /tmp/stats-depois.json)
```

Leitura dos deltas:

- **detail-cache**: `details.miss` sobe ~1 por leitura fria (PostgreSQL + gravação
  no cache); na fase warm `details.hit` domina (leituras no mesmo id dentro do
  `detailsTtlSeconds`).
- **search-canonical**: `search.miss` praticamente parado (só a primeira variação
  da consulta); `search.hit` cresce junto com o total de requisições.
- **search-distinct**: `search.miss` sobe ~1 por requisição (chave nova a cada
  consulta); hits raros.
- **burst-after-expiry**: a evidência do single-flight é o delta — rajada de 30
  VUs na mesma chave gera `search.miss == 2` no total (1 do prime + 1 do líder) e
  `search.hit == 29` (os seguidores revalidam o cache e contam hit). **Sem
  coalescing**, a mesma rajada produziria ~30 misses.
- **redis-down**: `details.error` e `search.error` crescem (cada operação com o
  cache indisponível é contada e contornada) — e `http_req_failed` continua em ~0.
- **es-down-writes**: `search.miss` cresce (a consulta nunca chega a popular);
  escritas não aparecem em nenhum contador de erro. Após `drain`, as vagas
  criadas durante a falha aparecem na busca.

## Nota honesta sobre os números

Os resultados desta tabela são medidos **localmente**, em container na máquina de
desenvolvimento, compartilhando CPU/disco com PostgreSQL, Elasticsearch, Redis, a
API e o navegador. Eles são **ruidosos**: servem para (a) ordem de grandeza
(p.ex. hit de Redis dezenas de vezes mais rápido que leitura fria), (b) provar
**ausência de erro** (thresholds de `http_req_failed` e de p95 em tetos
deliberadamente folgados) e (c) comprovar propriedades de contagem (coalescing,
deltas de miss/hit). **Não** são benchmark oficial de capacidade — para isso seriam
necessários ambiente dedicado, aquecimento, múltiplas repetições e análise
estatística das execuções.
