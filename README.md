# TechJobs — Catálogo de Vagas com Busca (Elasticsearch) e Cache (Redis)

API REST em **ASP.NET Core (.NET 10)** + frontend **React 18/TS**, orquestrando três
infraestruturas com papéis distintos:

- **PostgreSQL** — fonte da verdade transacional. Toda escrita (POST/PUT) é confirmada
  aqui antes de qualquer resposta de sucesso; o detalhe por ID sempre reflete o estado
  oficial (inclusive vaga `encerrada`).
- **Elasticsearch** — projeção de busca (índice `techjobs-v1`), otimizada para texto,
  filtros e agregações. Nunca é a fonte da verdade e pode ficar defasada por um
  intervalo curto.
- **Redis** — cache de leitura com expiração: detalhe por ID (cache-aside) e resultado
  de busca (chave canônica). Falha do Redis nunca derruba a API.

> A especificação completa do desafio está preservada em [`docs/DESAFIO-SPEC.md`](docs/DESAFIO-SPEC.md).

---

## Como executar

```bash
docker compose up --build
# API:          http://localhost:8080/health
# Frontend:     http://localhost:3000
# Elasticsearch: http://localhost:9200
# Swagger:      http://localhost:8080/swagger  (OpenAPI em /openapi/v1.json)
```

A stack sobe vazia e o schema do PostgreSQL + o índice do Elasticsearch são criados
automaticamente no startup (sem passos manuais). Popule a base com o seed
determinístico:

```bash
curl -X POST localhost:8080/admin/seed -H 'Content-Type: application/json' \
     -d '{"count": 5000, "seed": 42}'     # campos opcionais; defaults 5000/42
```

Configuração por ambiente (já definida no compose): `ConnectionStrings__Postgres`,
`ConnectionStrings__Redis`, `Elasticsearch__Url`, `Cache__DetailsTtlSeconds` (padrão
300), `Cache__SearchTtlSeconds` (padrão 60) e `ENABLE_TEST_ENDPOINTS` (endpoints
`/admin/*` — **nunca** em produção).

### Testes

```bash
dotnet test backend/TechJobs.slnx --filter "FullyQualifiedName~UnitTests"       # sem dependências
docker compose up -d --build                                                    # stack viva
TECHJOBS_API=http://localhost:8080 dotnet test backend/TechJobs.slnx \
  --filter "FullyQualifiedName~IntegrationTests"                                # black-box
cd frontend && npm test                                                         # vitest
```

62 testes de unidade + 44 de integração (contrato, cache e single-flight, outbox sob
concorrência, falhas simuladas, drain, reindex, expiração real de TTL, idempotência,
seed determinístico) + 11 testes vitest no frontend. Os de integração pulam (skip) se
a API não estiver no ar — **exceto em modo estrito** (`TECHJOBS_REQUIRE_API=true`,
usado no CI), em que a suíte **falha** em vez de skipar. O CI também sobe os perfis
extras do compose: `--profile adminoff` (segunda API em :8085 **sem**
`ENABLE_TEST_ENDPOINTS` — valida que `/admin/*` responde 404 sem a flag) e
`--profile shortttl` (API em :8086 com TTLs de 2s — valida a expiração **real** do
cache).

---

## Arquitetura e decisões

```
POST/PUT ──► slices CreateJob/UpdateJob ──► PostgreSQL (commit + outbox na MESMA transação)
                 │                         │
                 │ invalida cache detalhe   │ worker em background (canal + tick 1s)
                 ▼                         ▼
              Redis  ◄──┐            Elasticsearch (bulk com id estável)
                       │                         ▲
GET /api/jobs/{id} ────┤ cache-aside             │
GET /api/jobs/search ──┤ cache por chave canônica┤ agregações p/ stats
```

### Cache (Redis) — cache-aside, TTL, invalidação e chaves

- **Detalhe** (`job:details:v1:{id}`): miss → lê PostgreSQL → grava no Redis com TTL
  configurável (`Cache__DetailsTtlSeconds`, exibido em `/admin/cache-stats`). Após o
  commit do PUT a chave é **deletada** — o valor antigo não volta (até expirar por TTL
  em conjunto com a nova gravação).
- **Busca** (`job:search:v1:{gen}:{sha256}`): a chave deriva de uma **forma canônica**
  da consulta — `q` normalizado (trim, espaçamento colapsado, lowercase), tecnologias
  em lowercase/sem duplicatas/**ordenadas** (a semântica é E, então a ordem não altera
  o resultado), decimais sem zeros à direita, página/tamanho/ordenação explícitos. O
  JSON canônico é serializado de forma estável e passado por SHA-256: consultas
  idênticas reutilizam a chave; qualquer variação de filtro/página/ordenação não
  colide (coberto por testes de unidade).
- **Invalidação lógica por geração**: seed/reset/drain/reindex incrementam uma geração
  que faz parte da chave — chaves antigas ficam imediatamente inalcançáveis e expiram
  pelo TTL, sem SCAN/DEL em massa. A geração nasce com o timestamp atual para não
  colidir com chaves de execuções anteriores após restart.
- **Degradação graciosa**: qualquer falha do Redis (real ou simulada via
  `/admin/faults`) é contada (`error` em `/admin/cache-stats`) e o fluxo segue pela
  fonte oficial — nada responde 500 por culpa do Redis. Um DEL pós-commit que falha
  com o Redis fora fica **pendente** e é drenado na primeira operação Redis
  bem-sucedida — a chave velha não sobrevive à recuperação.
- **Single-flight** (`Platform/Runtime/SingleFlight.cs`): uma rajada de leituras sobre
  a mesma chave fria coalesce em **um único miss** (uma leitura do PostgreSQL + uma
  gravação no Redis), em vez de ~N misses. O gate por vaga é compartilhado entre o
  fill do GET e o PUT (commit + invalidação), coordenando as duas operações na
  instância única — medido com k6 (`burst-after-expiry`: 30 VUs → 2 misses no total,
  incluindo o prime; 29 hits) e coberto por testes.
- **Corrida conhecida da invalidação** (mitigada na instância única): entre o GET que
  leu o valor antigo do PostgreSQL e a gravação no cache, um PUT pode commitar e
  invalidar; o GET então regravaria o valor antigo, que sobrevive apenas até o TTL de
  detalhe. O gate por vaga + single-flight fecham essa janela **numa única instância
  da API**; múltiplas réplicas continuam sem coordenação distribuída (ver Limitações).

### Sincronização PostgreSQL ↔ Elasticsearch (eventualmente consistente)

1. O POST/PUT grava a vaga **e** um evento **imutável** em `outbox_events` **na mesma
   transação** (`event_id bigserial` como PK): cada escrita insere N linhas novas —
   reeditar uma vaga nunca reaproveita ou sobrescreve evento anterior. A migração é
   idempotente e roda no startup, convertendo bases antigas (uma linha por vaga) para
   o formato novo.
2. Um worker em background (sinal por canal + varredura a cada 1s) indexa em lote e
   remove **exatamente os `event_id` consumidos** após o bulk ser confirmado — eventos
   enfileirados em paralelo (outro PUT na mesma vaga durante o consumo) sobrevivem e
   são processados no tick seguinte.
3. Se a API cair entre commit e indexação, os eventos **sobrevivem** no outbox e são
   recuperados no restart do worker; se o Elasticsearch estiver fora, permanecem
   pendentes e são refeitos no próximo tick — sem perder o cadastro oficial. Um PUT
   concorrente ao consumo não desaparece mais (coberto por teste de integração).
4. Worker, `/admin/indexing/drain` e `/admin/indexing/reindex` passam por um gate de
   exclusão mútua (`SemaphoreSlim`/`RunExclusivelyAsync`) — só um opera o par
   PG→ES por vez. O drain força o estado consistente: aplica o pendente, faz refresh
   do índice e invalida o cache de busca. O reindex reconstrói o índice a partir de um
   snapshot **REPEATABLE READ** do PostgreSQL, marca watermark `max(event_id)` e remove
   só os eventos com `event_id <= watermark` — escrita que chega durante o reindex não
   se perde. Como o id do documento é o id da vaga (upsert), reindexar nunca duplica.
5. A janela de inconsistência típica é ≤ ~1s (tick do worker + refresh do ES, que por
   padrão é 1s); o cache de busca adiciona até `SearchTtlSeconds` de defasagem em
   consultas repetidas — por isso o drain invalida a geração.

### Modelagem

- **Tecnologias** em `jsonb` no PostgreSQL: preserva grafia/ordem, dedup
  case-insensitive garantido pela validação, e evita tabela normalizada (leitura sem
  JOIN). No índice existem duas projeções: `technologies` (keyword, grafia original —
  usada na exibição e na agregação de stats) e `techKeys` (keyword, lowercase — usada
  no filtro case-insensitive, já que `term` em keyword é case-sensitive).
- **Salários**: `numeric(14,2)` no PostgreSQL (valor autoritativo, decimal exato). A
  validação normaliza para 2 casas (arredondamento `AwayFromZero`, mesmo critério do
  `numeric(14,2)`) e rejeita valores acima de 999999999999.99 com 400. POST/PUT fazem
  **read-back na mesma transação**: a resposta devolve exatamente o valor oficial que
  um GET subsequente leria. No índice o campo é `scaled_float` com `scaling_factor`
  100 (escala de centavos definida); um índice antigo em `double` é detectado via
  GetMapping e reconstruído + reindexado automaticamente no startup. O contrato
  permanece decimal de ponta a ponta na API.
- **Status**: vagas `encerrada` continuam no índice (o documento é atualizado, não
  removido), mas um filtro `status=aberta` permanente as exclui de
  `/api/jobs/search` e `/api/jobs/stats`; o detalhe por ID segue servindo o estado
  oficial pela fonte PostgreSQL.
- **Idempotência do PUT**: substituição completa; se o conteúdo de domínio não muda,
  `updatedAt` é preservado — reaplicar o mesmo payload deixa a vaga exatamente no
  mesmo estado (verificado por teste).

### Busca e relevância

- `multi_match` **`cross_fields`** em `title^3` e `description` com `operator: and` —
  título pesa 3× a descrição, múltiplos termos usam semântica E (todos presentes) e os
  termos podem estar **distribuídos entre título e descrição** (parte no título, parte
  na descrição conta como match — antes, com `best_fields`, a frase inteira precisava
  caber num único campo).
- **Paginação**: o índice é criado/atualizado com `max_result_window` de 50.000
  (setting dinâmico, aplicado também em índices existentes) e o offset é calculado em
  `long` (sem overflow); página além da janela repete a consulta com `size: 0` e
  responde 200 com `items: []` e `total` correto — o seed de 20k é navegável além da
  marca de 10k.
- Sem `q`, a consulta é `match_all` filtrada (nunca `multi_match` vazio) e
  `relevance` degrada para `newest` (`publishedAt` desc, empate por `id` asc).
  `salary_desc` ordena por `salaryMax` desc com vagas sem salário por último
  (`missing: _last`, empate por `id` asc).
- Filtros salariais usam **interseção inclusiva de intervalos**
  (`salaryMax >= minSalary` E `salaryMin <= maxSalary`); range em campo ausente não
  casa, logo vagas sem faixa salarial ficam fora de qualquer filtro salarial.
- **Impacto do analyzer `portuguese`** (exigido pela spec): stemming e stopwords em
  PT-BR. Consequências práticas: "desenvolvedora" encontra "desenvolvedor" (stem
  comum); stopwords como *"fora"* são removidas da consulta; termos com ponto
  interno viram um token só e são stemados ("Node.js" → "node.j"), então buscar
  "node" **não** encontra "Node.js" — buscar "Node.js" ou "node.js" encontra. A
  mesma análise é aplicada na indexação e na busca, então termos equivalentes são
  consistentes. Nota honesta: o inverso não vale para acentos — "programacao" **não**
  encontra "programação", porque no analyzer `portuguese` o folding de acentos ocorre
  após o stemming (verificado com o stemmer RSLP real).

### Estatísticas (`/api/jobs/stats`)

Derivadas de agregações `terms` do índice (nunca números fixos), apenas de vagas
`aberta`: total (contagem da consulta), `bySeniority`/`byWorkMode` com **todas** as
chaves do enum (0 quando vazio — por isso somam exatamente `totalOpenJobs`) e
`topTechnologies` (top 10 por ocorrências). Como uma vaga contribui para várias
tecnologias, a soma do top pode exceder `totalOpenJobs` — comportamento esperado da
agregação multi-valor.

### Falhas simuladas e degradação (dev-only)

`POST /admin/faults {"target":"redis"|"elasticsearch","enabled":true}`:

| Falha | Detalhe por ID | Busca / Stats | Escrita (POST/PUT) |
|---|---|---|---|
| Redis | 200 via PostgreSQL (`error++`) | 200 via Elasticsearch sem cache (`error++`) | 201/200 normal |
| Elasticsearch | 200 normal | **503** Problem Details controlado | **201/200 confirmado no PostgreSQL**; indexação fica pendente |

Desligar a falha + `/admin/indexing/drain` devolve o sistema ao estado consistente.

### Erros

Problem Details (RFC 9457) com `type`/`title`/`status`/`detail` e
`Content-Type: application/problem+json`: 400 validação (mensagens nomeiam o campo e o
valor, ex.: `salaryMax (5000) deve ser maior ou igual a salaryMin (6500).`), 404 vaga
não encontrada, 503 busca indisponível, 500 inesperado sem stack trace. Conteúdo de
texto livre não é ecoado — `title`/`company`/`description` aparecem nas mensagens e
logs apenas pelo **comprimento** (enums e salários mantêm o eco, baixo risco). Logs
estruturados sem strings de conexão e sem o `detail` da exceção.

---

## CI/CD

- **GitHub Actions** (`.github/workflows/ci.yml`): a cada push/PR — testes de unidade,
  `docker compose up` (com os perfis `adminoff` + `shortttl`), smoke de saúde e a
  suíte de integração completa em **modo estrito** (`TECHJOBS_REQUIRE_API=true`: a
  suíte falha sem API, não skipa; resultado `NotExecuted` falha o job), com os TRX
  publicados como artefato.
- **Azure DevOps** (`azure-pipelines.yml` + `pipelines/templates/dotnet-build-test.yml`):
  stages Build → Test → Package → DeployStaging → DeployProduction com deployment jobs
  por `environment`; a imagem é pinada pelo SHA de `BUILD_SOURCEVERSION` e o push usa
  `Docker@2` com a service connection `techjobs-registry`; a aprovação de produção é
  um **check do Environment** (não declarável em YAML). Evidências, rollback por
  digest, pré-requisitos externos e o executado×proposto em
  [`docs/pipeline-evidence.md`](docs/pipeline-evidence.md).
- **Carga e demo**: cenários k6 versionados em [`tests/load/`](tests/load/) (resultados
  medidos em [`docs/performance.md`](docs/performance.md)) e roteiro de demonstração
  de 11 passos em [`docs/demo.md`](docs/demo.md).

## Estrutura

```
backend/
  src/TechJobs.Api/
    Contracts/               # contrato de API congelado: DTOs, exceções de contrato, handler global
    JobsCore/                # agregado Job (modelos, validação, JobDoc) + Persistence/ (JobStore, outbox)
    Slices/                  # 1 rota pública = 1 slice (endpoint + handler do fluxo):
                             #   CreateJob/, UpdateJob/, GetJobById/, SearchJobs/, JobStats/
    Operations/              # endpoints dev-only (/admin/*): reset, seed, faults, cache-stats, drain, reindex
    Platform/                # transversal: Database (PG), Elastic/ (índice de busca + worker de
                             #   indexação + reconstrução de índice), Runtime/ (cache Redis +
                             #   single-flight, falhas, contadores), StartupInitializer
  tests/TechJobs.UnitTests/    # validação, chave canônica, parser de busca, seed determinístico
  tests/TechJobs.IntegrationTests/  # black-box contra a stack (contrato, cache, single-flight,
                                    #   outbox concorrente, falhas, TTL)
frontend/src/                  # pages/, components/, services (api.ts), types.ts, salary.ts
tests/load/                    # cenários k6 (detalhe, busca canônica/distinta, burst, falhas) + run.sh
pipelines/templates/           # template YAML reutilizável do Azure DevOps
docs/                          # adr/0001-vertical-slices.md, pipeline-evidence.md, performance.md,
                               #   demo.md, DESAFIO-SPEC.md
```

## Trade-offs e limitações conhecidas

- **Contadores de cache em memória** (`/admin/cache-stats`): corretos para a
  arquitetura de avaliação (uma instância da API); em múltiplas réplicas migrariam
  para o Redis (INCR) ou Prometheus. O mesmo vale para o single-flight e para o gate
  de indexação (worker/drain/reindex): são locks **in-process**, válidos para a
  instância única — não há coordenação distribuída entre réplicas.
- **Invaliação de detalhe via DEL** após commit: a corrida clássica (GET regravar o
  valor antigo) está fechada na instância única por gate por vaga + single-flight;
  entre réplicas, restaria a janela de ms limitada pelo TTL.
- **Seed/reseta limpam o Redis com FLUSHDB** (dev-only). O detail-cache não tem
  geração versionada — após um flush malsucedido, chaves órfãs de ids inexistentes
  expiram pelo TTL.
- **`relevance` com `q`** desempata por score/_doc do Elasticsearch (empates não são
  amarrados ao `id`, apenas `newest` e `salary_desc` o são, como a spec pede).
- **Sem paginação por scroll/search_after**: o contrato usa page/pageSize com
  `track_total_hits` exato e a janela foi ampliada para 50.000 — navegação além disso
  continuaria exigindo scroll/search_after.
- **Frontend** é funcional e enxuto (sem identidade visual elaborada, conforme a spec:
  5% da nota); navegação por estado em vez de roteador para manter o bundle mínimo.
- **Outbox-lite**: eventos imutáveis + worker com retry periódico + reindex com
  watermark transacional — PUT concorrente ao consumo/reindex não se perde (coberto
  por teste), mas não é o transactional outbox completo (sem dead-letter/backoff
  exponencial persistente); diferencial apontado na própria spec como opcional.

## Perguntas da apresentação técnica

As respostas estão embutidas nas seções acima — cache-aside e TTL em **Cache**;
stampede/single-flight em **Cache** (limite entre réplicas em *Limitações*); corrida
de invalidação em **Cache**; `match`×`term` /
`text`×`keyword` em **Modelagem** (techKeys) e **Busca**; ES para texto × PG para
detalhe em **Arquitetura**; queda de 20min do ES em **Sincronização** (outbox + drain);
Azure Pipelines em **CI/CD** e `docs/pipeline-evidence.md`; janela de inconsistência em
**Sincronização**; simplificações do MVP em **Limitações**.
