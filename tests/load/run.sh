#!/usr/bin/env bash
# ============================================================================
# tests/load/run.sh — runner dos cenários de carga k6 do TechJobs.
#
# AVISO — leia antes de rodar:
#   * Os cenários usam endpoints DEV-ONLY `/admin/*` (reset/seed/faults):
#     reset/seed SUBSTITUEM toda a base (PostgreSQL + índice do Elasticsearch +
#     Redis) e ligar falhas simuladas altera o comportamento da API.
#     Rode SOMENTE contra uma stack DESCARTÁVEL:
#       docker compose up -d --build && curl -s localhost:8080/health
#   * redis-down e es-down-writes LIGAM falhas simuladas e as DESLIGAM no
#     teardown. Se o processo for morto antes do fim, restaure manualmente:
#       curl -X POST "$BASE_URL/admin/faults" -H 'Content-Type: application/json' \
#            -d '{"target":"redis","enabled":false}'
#       curl -X POST "$BASE_URL/admin/faults" -H 'Content-Type: application/json' \
#            -d '{"target":"elasticsearch","enabled":false}'
#
# Uso:
#   ./run.sh                    # 'all': os 4 cenários SEM falha simulada
#   ./run.sh detail-cache       # um cenário específico (qualquer um)
#   ./run.sh redis-down         # cenários de falha: rode ISOLADOS, um por vez
#   ./run.sh es-down-writes
#
# Variáveis:
#   BASE_URL   URL da API (default http://localhost:8080; override local: 18080)
#   K6_IMAGE   imagem do k6 (default grafana/k6:1.0.0 — versão fixada)
#   VUS, DURATION_SECONDS, SEED_COUNT, BURST_VUS  passam direto ao k6:
#     VUS=5 DURATION_SECONDS=15 ./run.sh search-canonical
#
# Resultados: thresholds no stdout de cada cenário; contadores de cache
# (hit/miss/error) são lidos manualmente via /admin/cache-stats antes x depois
# — ver docs/performance.md.
# ============================================================================
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
K6_IMAGE="${K6_IMAGE:-grafana/k6:1.0.0}"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Cenários sem falha simulada (podem rodar em sequência).
CENARIOS_SEGUROS=(detail-cache search-canonical search-distinct burst-after-expiry)
# Cenários com falha simulada (rodar isolados, um por vez).
CENARIOS_FALHA=(redis-down es-down-writes)

run_k6() {
  local script="$1"
  if command -v k6 >/dev/null 2>&1; then
    echo ">>> k6 nativo: ${script}"
    k6 run -e BASE_URL="${BASE_URL}" "${script}"
  else
    echo ">>> k6 via Docker (${K6_IMAGE}): ${script}"
    # Monta o diretório em vez de usar stdin (`run -`): os cenários importam
    # ./lib.js e imports relativos não resolvem com script vindo do stdin.
    docker run --rm --network host \
      -e BASE_URL="${BASE_URL}" \
      -v "${DIR}:/scripts" -w /scripts \
      "${K6_IMAGE}" run "/scripts/$(basename "${script}")"
  fi
}

cenario_valido() {
  local s="$1"
  for conhecido in "${CENARIOS_SEGUROS[@]}" "${CENARIOS_FALHA[@]}"; do
    [[ "$s" == "$conhecido" ]] && return 0
  done
  return 1
}

SCENARIO="${1:-all}"
case "$SCENARIO" in
  all)
    echo "Base: ${BASE_URL} — 'all' NÃO liga falhas simuladas."
    for s in "${CENARIOS_SEGUROS[@]}"; do
      echo ""
      echo "===== CENÁRIO ${s} ====="
      run_k6 "${DIR}/${s}.js"
    done
    ;;
  *)
    if ! cenario_valido "$SCENARIO"; then
      echo "Cenário desconhecido: '${SCENARIO}'" >&2
      echo "Uso: $0 [all|${CENARIOS_SEGUROS[*]}|${CENARIOS_FALHA[*]}]" >&2
      exit 1
    fi
    for f in "${CENARIOS_FALHA[@]}"; do
      if [[ "$SCENARIO" == "$f" ]]; then
        echo "AVISO: '${SCENARIO}' liga falha simulada (/admin/faults) e desliga no teardown."
      fi
    done
    run_k6 "${DIR}/${SCENARIO}.js"
    ;;
esac
