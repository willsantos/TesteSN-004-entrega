# Demo TechJobs — roteiro de avaliação (5–10 min)

Roteiro passo a passo com comandos copy-paste. Cada passo mostra uma propriedade
do sistema: fonte da verdade (PostgreSQL), projeção de busca (Elasticsearch),
cache de leitura (Redis), degradação graciosa e recuperação.

**Pré-requisitos:** `docker` (stack), `curl` e `jq` (opcional, mas os comandos
abaixo o usam para resumir o JSON). Swagger em `http://localhost:8080/swagger`.

> **Porta local:** os comandos usam a porta oficial **8080**. Se sua máquina usa o
> override do compose (`docker-compose.override.yml`), troque uma linha só:
> `export BASE=http://localhost:18080` (a nota está no passo 0).

Mantenha **um único terminal**: as variáveis `$BASE`, `$ID` etc. persistem entre
os passos. Tempo total: ~8 minutos (o seed de 20000 no passo 11 é o mais lento).

```bash
# ---------------------------------------------------------------------------
# Passo 0 — variáveis da sessão
# ---------------------------------------------------------------------------
export BASE=http://localhost:8080     # override local do compose: 18080
```

## 1. Subida da stack + health (~1 min)

```bash
docker compose up -d --build
until curl -sf "$BASE/health"; do sleep 2; done    # espera o startup terminar
```

**Observar:** `{"status":"ok"}` — PostgreSQL, Redis e Elasticsearch sobem com
healthcheck; o schema do PG e o índice do ES são criados no startup, sem passos
manuais. **Dizer:** "a stack orquestra três infraestruturas com papéis distintos."

## 2. Seed determinístico 5000 — reprodutibilidade (~20s)

```bash
curl -s -X POST "$BASE/admin/seed" -H 'Content-Type: application/json' \
     -d '{"count":5000,"seed":42}' | jq -c '.'
ANTES=$(curl -s "$BASE/api/jobs/search?pageSize=1" | jq -r '"\(.total) \(.items[0].id)"')
echo "1ª execução: $ANTES"

curl -s -X POST "$BASE/admin/seed" -H 'Content-Type: application/json' \
     -d '{"count":5000,"seed":42}' | jq -c '.'
DEPOIS=$(curl -s "$BASE/api/jobs/search?pageSize=1" | jq -r '"\(.total) \(.items[0].id)"')
echo "2ª execução: $DEPOIS"
[ "$ANTES" = "$DEPOIS" ] && echo "IDÊNTICOS: mesmo (count, seed) => mesma base"
```

**Observar:** mesmos `total` e mesmo primeiro `id` nas duas execuções (mesmos
títulos, salários, datas — conteúdo completo). **Dizer:** "o seed é determinístico:
mesmo count+seed regenera exatamente o mesmo conteúdo, inclusive ids." O total é
menor que 5000 porque ~12% nascem `encerrada` e ficam fora da busca.

## 3. Busca com filtros + paginação (~30s)

```bash
curl -s -G "$BASE/api/jobs/search" \
     --data-urlencode "q=desenvolvedor" \
     --data-urlencode "seniority=senior" \
     --data-urlencode "workMode=remoto" \
     --data-urlencode "sort=salary_desc" \
     --data-urlencode "page=2" --data-urlencode "pageSize=5" \
| jq '{page, pageSize, total, itensNestaPagina: (.items|length), salarios: [.items[].salaryMax]}'
```

**Observar:** `total` (todas as abertas que casam, com `track_total_hits` exato),
pagina estável ordenada por `salaryMax` desc, 5 itens. **Dizer:** "busca no
Elasticsearch: multi_match título 3× descrição, semântica E, filtros exatos."

## 4. Cache: miss → hit via cache-stats + TTL (~40s)

```bash
curl -s "$BASE/admin/cache-stats" | jq '{config, details, search}'   # anote os deltas

ID=$(curl -s "$BASE/api/jobs/search?pageSize=1" | jq -r '.items[0].id')
curl -s -o /dev/null -w '1º GET detalhe -> %{http_code}\n' "$BASE/api/jobs/$ID"
curl -s "$BASE/admin/cache-stats" | jq '.details'     # miss +1 (leu PostgreSQL)
curl -s -o /dev/null -w '2º GET detalhe -> %{http_code}\n' "$BASE/api/jobs/$ID"
curl -s "$BASE/admin/cache-stats" | jq '.details'     # hit +1 (leu Redis)

curl -s -o /dev/null "$BASE/api/jobs/search?q=redis&pageSize=10"   # 1ª busca
curl -s "$BASE/admin/cache-stats" | jq '.search'      # miss +1 (chave canônica nova)
curl -s -o /dev/null "$BASE/api/jobs/search?q=redis&pageSize=10"   # MESMA busca
curl -s "$BASE/admin/cache-stats" | jq '.search'      # hit +1
```

**Observar:** `config.detailsTtlSeconds` (300) e `config.searchTtlSeconds` (60) —
os hits valem dentro do TTL. **Dizer:** "cache-aside para detalhe e cache por
chave canônica (sha256) para busca; contadores de hit/miss evidenciam o caminho."

## 5. POST + PUT e detalhe atualizado — centavos coerentes (~1 min)

```bash
NOVA=$(curl -s -X POST "$BASE/api/jobs" -H 'Content-Type: application/json' -d '{
  "title": "Engenheira de Plataforma Kubernetes",
  "company": "Demo Tech",
  "description": "Vaga de demonstração criada para evidenciar a escrita oficial no PostgreSQL e a projeção no Elasticsearch com centavos exatos.",
  "technologies": ["Kubernetes", "AWS"],
  "seniority": "senior", "workMode": "remoto", "city": null,
  "salaryMin": 18000.55, "salaryMax": 24000.75, "status": "aberta" }')
export ID=$(echo "$NOVA" | jq -r '.id'); echo "criada: $ID"

curl -s "$BASE/api/jobs/$ID" | jq '{salaryMin, salaryMax}'
# -> 18000.55 / 24000.75: GET devolve EXATAMENTE os centavos enviados no POST

curl -s -X PUT "$BASE/api/jobs/$ID" -H 'Content-Type: application/json' -d '{
  "title": "Engenheira de Plataforma Kubernetes",
  "company": "Demo Tech",
  "description": "Vaga de demonstração criada para evidenciar a escrita oficial no PostgreSQL e a projeção no Elasticsearch com centavos exatos.",
  "technologies": ["Kubernetes", "AWS"],
  "seniority": "senior", "workMode": "remoto", "city": null,
  "salaryMin": 18000.55, "salaryMax": 26000.42, "status": "aberta" }' | jq '{salaryMax, updatedAt}'

curl -s "$BASE/api/jobs/$ID" | jq '{salaryMax, updatedAt}'
```

**Observar:** o GET logo após o PUT já traz `26000.42` — o cache de detalhe é
**invalidado no commit** (o valor antigo não volta). **Dizer:** "PUT substitui por
completo, confirma no PostgreSQL e deleta a chave de cache; salário é
`numeric(14,2)` — centavos idênticos POST/PUT × GET."

## 6. Encerrar vaga + drain: sai da busca, continua no detalhe (~40s)

```bash
curl -s -X POST "$BASE/admin/indexing/drain" | jq -c '.'
curl -s -G "$BASE/api/jobs/search" --data-urlencode "q=Engenheira Plataforma Kubernetes" \
| jq '{total}'                                        # total: 1 (aberta e indexada)

curl -s -X PUT "$BASE/api/jobs/$ID" -H 'Content-Type: application/json' -d '{
  "title": "Engenheira de Plataforma Kubernetes",
  "company": "Demo Tech",
  "description": "Vaga de demonstração criada para evidenciar a escrita oficial no PostgreSQL e a projeção no Elasticsearch com centavos exatos.",
  "technologies": ["Kubernetes", "AWS"],
  "seniority": "senior", "workMode": "remoto", "city": null,
  "salaryMin": 18000.55, "salaryMax": 26000.42, "status": "encerrada" }' > /dev/null

curl -s -X POST "$BASE/admin/indexing/drain" | jq -c '.'
curl -s -G "$BASE/api/jobs/search" --data-urlencode "q=Engenheira Plataforma Kubernetes" \
| jq '{total}'                                        # total: 0
curl -s "$BASE/api/jobs/$ID" | jq '{status, salaryMax}'   # detalhe segue oficial
```

**Observar:** após o PUT + `drain`, a vaga **sai da busca** (filtro permanente
`status=aberta`), mas o **detalhe por id continua 200** servido pelo PostgreSQL.
**Dizer:** "o drain força consistência (aplica o outbox, refresca o índice e
invalida o cache de busca); sem ele a busca repetida poderia ficar defasada até o
TTL — a indexação é eventualmente consistente por desenho."

## 7. Redis em falha: nenhuma leitura vira 500 (~40s)

```bash
curl -s -X POST "$BASE/admin/faults" -H 'Content-Type: application/json' \
     -d '{"target":"redis","enabled":true}' | jq -c '.'

curl -s -o /dev/null -w 'GET detalhe  -> %{http_code}\n' "$BASE/api/jobs/$ID"   # 200 via PG
curl -s -o /dev/null -w 'GET busca    -> %{http_code}\n' "$BASE/api/jobs/search?q=desenvolvedor&pageSize=10"   # 200 via ES
curl -s -o /dev/null -w 'POST/PUT     -> ' -X PUT "$BASE/api/jobs/$ID" \
     -H 'Content-Type: application/json' -d '{
  "title": "Engenheira de Plataforma Kubernetes", "company": "Demo Tech",
  "description": "Vaga de demonstração criada para evidenciar a escrita oficial no PostgreSQL e a projeção no Elasticsearch com centavos exatos.",
  "technologies": ["Kubernetes", "AWS"], "seniority": "senior", "workMode": "remoto",
  "city": null, "salaryMin": 18000.55, "salaryMax": 26000.42, "status": "encerrada" }'
curl -s -o /dev/null -w '%{http_code}\n'

curl -s "$BASE/admin/cache-stats" | jq '{details, search}'   # error cresceu
curl -s -X POST "$BASE/admin/faults" -H 'Content-Type: application/json' \
     -d '{"target":"redis","enabled":false}' | jq -c '.'     # restaura
```

**Observar:** todos os status continuam 200; `details.error` e `search.error`
sobem — a falha de cache é **contada e contornada** (leitura direto na fonte),
nunca propagada como 5xx. **Dizer:** "Redis é otimização, não dependência."

## 8. Elasticsearch em falha: busca 503, escrita preservada (~40s)

```bash
curl -s -X POST "$BASE/admin/faults" -H 'Content-Type: application/json' \
     -d '{"target":"elasticsearch","enabled":true}' | jq -c '.'

curl -s -w '\nHTTP %{http_code}\n' "$BASE/api/jobs/search?q=desenvolvedor&pageSize=10" \
| jq -c 'if .title then {title, status} else . end'    # 503 Problem Details controlado

RESILIENTE=$(curl -s -X POST "$BASE/api/jobs" -H 'Content-Type: application/json' -d '{
  "title": "Vaga de Demonstração Resiliente",
  "company": "Demo Tech",
  "description": "Vaga de demonstração resiliente criada para provar que a escrita no PostgreSQL sobrevive à queda do Elasticsearch via outbox.",
  "technologies": ["PostgreSQL", "Docker"],
  "seniority": "pleno", "workMode": "hibrido", "city": "Curitiba",
  "salaryMin": 8000.10, "salaryMax": 12000.90, "status": "aberta" }')
export ID2=$(echo "$RESILIENTE" | jq -r '.id'); echo "POST com ES fora -> id $ID2"

curl -s -X PUT "$BASE/api/jobs/$ID2" -H 'Content-Type: application/json' -d '{
  "title": "Vaga de Demonstração Resiliente",
  "company": "Demo Tech",
  "description": "Vaga de demonstração resiliente criada para provar que a escrita no PostgreSQL sobrevive à queda do Elasticsearch via outbox.",
  "technologies": ["PostgreSQL", "Docker"],
  "seniority": "pleno", "workMode": "hibrido", "city": "Curitiba",
  "salaryMin": 8000.10, "salaryMax": 13500.30, "status": "aberta" }' | jq '{salaryMax}'
```

**Observar:** busca responde **503 Problem Details** (contrato controlado, não um
500 genérico); o POST devolve **201** e o PUT **200** com `salaryMax` 13500.30 —
escritas confirmadas no PostgreSQL; a indexação fica **pendente no outbox**.
**Dizer:** "o ES nunca é a fonte da verdade: a escrita oficial acontece na mesma
transação do outbox."

## 9. Recuperação + drain: tudo visível de novo (~20s)

```bash
curl -s -X POST "$BASE/admin/faults" -H 'Content-Type: application/json' \
     -d '{"target":"elasticsearch","enabled":false}' | jq -c '.'
curl -s -X POST "$BASE/admin/indexing/drain" | jq -c '.'

curl -s -G "$BASE/api/jobs/search" --data-urlencode "q=Demonstração Resiliente" \
| jq '{total, itens: [.items[] | {title, salaryMax}]}'
curl -s "$BASE/api/jobs/$ID2" | jq '{salaryMax, updatedAt}'
```

**Observar:** a busca volta a 200 e a vaga criada **durante a falha** aparece —
com o `salaryMax` do PUT (13500.30). **Dizer:** "o worker do outbox reprocessou o
pendência assim que o ES voltou; o drain garantiu o estado consistente."

## 10. Reindex 2×: total inalterado, nada duplica (~30s)

```bash
TOTAL=$(curl -s "$BASE/api/jobs/search?pageSize=1" | jq '.total'); echo "total busca: $TOTAL"
curl -s -X POST "$BASE/admin/indexing/reindex" | jq -c '.'   # {"count":N}
curl -s -X POST "$BASE/admin/indexing/reindex" | jq -c '.'   # mesmo count
echo "total busca depois: $(curl -s "$BASE/api/jobs/search?pageSize=1" | jq '.total')"
```

**Observar:** o `count` do reindex é o total de vagas no PostgreSQL (inclui
encerradas — pode ser maior que o total da busca, que só conta abertas); o que
importa: o **total da busca é igual antes e depois**. **Dizer:** "o documento no
índice usa o id da vaga (upsert) — reindexar quantas vezes quiser nunca duplica."

## 11. Paginação além de 10k (~1 min, seed grande)

```bash
curl -s -X POST "$BASE/admin/seed" -H 'Content-Type: application/json' \
     -d '{"count":20000,"seed":42}' | jq -c '.'
curl -s "$BASE/api/jobs/search?page=1001&pageSize=10" \
| jq '{page, total, itens: (.items|length), primeiro: .items[0].id}'
# página além da janela do índice (offset 50.000): 200 com items vazio e total real
curl -s "$BASE/api/jobs/search?page=6001&pageSize=10" \
| jq '{total, itens: (.items|length)}'
```

**Observar:** `page=1001` (offset 10.000, além da janela padrão do Elasticsearch
de 10.000) retorna itens normalmente — o índice é criado com
`max_result_window=50000`; e uma página **além do fim** responde 200 com `items`
vazio e o `total` real, nunca 500/503. **Dizer:** "o contrato de paginação é
estável em qualquer profundidade." (Para encerrar a demo, deixe a stack no estado
que quiser: o seed do passo 2 recria a base de 5000.)

---

## Perguntas frequentes da avaliação

As respostas completas estão no [`README.md`](../README.md):

- **Como funciona o cache?** — cache-aside por detalhe (TTL + invalidação no
  commit do PUT) e cache de busca por **chave canônica** (sha256 da forma
  normalizada da consulta). README, seção *Cache (Redis)*.
- **Como PostgreSQL e Elasticsearch se mantêm sincronizados?** — *outbox* na
  mesma transação da escrita + worker em background (tick de ~1s);
  `/admin/indexing/drain` força consistência. README, seção *Sincronização*.
- **Quem é a fonte da verdade?** — sempre o PostgreSQL; o ES é projeção de busca
  e o Redis é cache de leitura. README, seção *Arquitetura e decisões*.
- **Por que a busca responde 503 com o ES fora e a escrita não falha?** — tabela
  de degradação em *Falhas simuladas*; a escrita só depende do PostgreSQL + outbox.
- **Quais são as limitações conhecidas?** — README, seção *Trade-offs e
  limitações* (contadores em memória, corrida de invalidação de detalhe, etc.).
- **Como medir performance?** — `tests/load/` + [`performance.md`](performance.md).
