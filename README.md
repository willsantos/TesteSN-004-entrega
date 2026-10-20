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
```

53 testes de unidade + 26 de integração (contrato, cache, falhas simuladas, drain,
reindex, idempotência, seed determinístico). Os de integração pulam (skip) se a API
não estiver no ar.

---

## Arquitetura e decisões

```
POST/PUT ──► JobsService ──► PostgreSQL (commit + outbox na MESMA transação)
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
  fonte oficial — nada responde 500 por culpa do Redis.
- **Corrida conhecida da invalidação** (documentada, aceita): entre o GET que leu o
  valor antigo do PostgreSQL e a gravação no cache, um PUT pode commitar e invalidar;
  o GET então regravaria o valor antigo, que sobrevive apenas até o TTL de detalhe.
  Janela de milissegundos, mitigável com lock de single-flight (diferencial não
  implementado; ver Limitações).

### Sincronização PostgreSQL ↔ Elasticsearch (eventualmente consistente)

1. O POST/PUT grava a vaga **e** um evento em `outbox_events` **na mesma transação**.
2. Um worker em background (sinal por canal + varredura a cada 1s) indexa em lote e só
   remove o evento após o bulk ser confirmado.
3. Se a API cair entre commit e indexação, o evento **sobrevive** no outbox e é
   recuperado no restart do worker; se o Elasticsearch estiver fora, o evento permanece
   pendente e é refeito no próximo tick — sem perder o cadastro oficial.
4. `/admin/indexing/drain` força o estado consistente: aplica o pendente, faz refresh
   do índice e invalida o cache de busca. `/admin/indexing/reindex` reconstrói o índice
   inteiro a partir do PostgreSQL; como o id do documento é o id da vaga (upsert),
   reindexar nunca duplica.
5. A janela de inconsistência típica é ≤ ~1s (tick do worker + refresh do ES, que por
   padrão é 1s); o cache de busca adiciona até `SearchTtlSeconds` de defasagem em
   consultas repetidas — por isso o drain invalida a geração.

### Modelagem

- **Tecnologias** em `jsonb` no PostgreSQL: preserva grafia/ordem, dedup
  case-insensitive garantido pela validação, e evita tabela normalizada (leitura sem
  JOIN). No índice existem duas projeções: `technologies` (keyword, grafia original —
  usada na exibição e na agregação de stats) e `techKeys` (keyword, lowercase — usada
  no filtro case-insensitive, já que `term` em keyword é case-sensitive).
- **Salários**: `numeric(14,2)` no PostgreSQL (valor autoritativo, decimal exato) e
  `double` no Elasticsearch — cópia para busca/filtro, suficiente para as faixas em
  BRL; o contrato permanece decimal de ponta a ponta na API.
- **Status**: vagas `encerrada` continuam no índice (o documento é atualizado, não
  removido), mas um filtro `status=aberta` permanente as exclui de
  `/api/jobs/search` e `/api/jobs/stats`; o detalhe por ID segue servindo o estado
  oficial pela fonte PostgreSQL.
- **Idempotência do PUT**: substituição completa; se o conteúdo de domínio não muda,
  `updatedAt` é preservado — reaplicar o mesmo payload deixa a vaga exatamente no
  mesmo estado (verificado por teste).

### Busca e relevância

- `multi_match` em `title^3` e `description` com `operator: and` — título pesa 3× a
  descrição e múltiplos termos usam semântica E (todos presentes).
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
  consistentes.

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
não encontrada, 503 busca indisponível, 500 inesperado sem stack trace. Logs
estruturados sem strings de conexão.

---

## CI/CD

- **GitHub Actions** (`.github/workflows/ci.yml`): a cada push/PR — testes de unidade,
  `docker compose up`, smoke de saúde e a suíte de integração completa.
- **Azure DevOps** (`azure-pipelines.yml` + `pipelines/templates/dotnet-build-test.yml`):
  stages Build → Test → Package → DeployStaging → DeployProduction com deployment jobs
  por `environment`; a aprovação de produção é um **check do Environment** (não
  declarável em YAML). Evidências, rollback e política de segredos em
  [`docs/pipeline-evidence.md`](docs/pipeline-evidence.md).

## Estrutura

```
backend/
  src/TechJobs.Api/            # Program.cs, Domain/, Infrastructure/ (PG, Redis, ES, seed), Services/, Endpoints/
  tests/TechJobs.UnitTests/    # validação, chave canônica, parser de busca, seed determinístico
  tests/TechJobs.IntegrationTests/  # black-box contra a stack (contrato, cache, falhas)
frontend/src/                  # pages/, components/, services (api.ts), types.ts
pipelines/templates/           # template YAML reutilizável do Azure DevOps
docs/                          # pipeline-evidence.md, DESAFIO-SPEC.md (spec original)
```

## Trade-offs e limitações conhecidas

- **Contadores de cache em memória** (`/admin/cache-stats`): corretos para a
  arquitetura de avaliação (uma instância da API); em múltiplas réplicas migrariam
  para o Redis (INCR) ou Prometheus.
- **Invaliação de detalhe via DEL** após commit: sujeita à corrida descrita acima
  (janela de ms, limitada pelo TTL). Single-flight/lock distribuído ficaram como
  diferencial não implementado.
- **Seed/reseta limpam o Redis com FLUSHDB** (dev-only). O detail-cache não tem
  geração versionada — após um flush malsucedido, chaves órfãs de ids inexistentes
  expiram pelo TTL.
- **`relevance` com `q`** desempata por score/_doc do Elasticsearch (empates não são
  amarrados ao `id`, apenas `newest` e `salary_desc` o são, como a spec pede).
- **Sem paginação por scroll/search_after** para navegação profunda (contrato usa
  page/pageSize com `track_total_hits` exato).
- **Frontend** é funcional e enxuto (sem identidade visual elaborada, conforme a spec:
  5% da nota); navegação por estado em vez de roteador para manter o bundle mínimo.
- **Outbox-lite**: tabela `outbox_events` + worker com retry periódico — não é o
  transactional outbox completo (sem dead-letter/exponential backoff persistente);
  diferencial apontado na própria spec como opcional.

## Perguntas da apresentação técnica

As respostas estão embutidas nas seções acima — cache-aside e TTL em **Cache**;
stampede/evicção em *Limitações*; corrida de invalidação em **Cache**; `match`×`term` /
`text`×`keyword` em **Modelagem** (techKeys) e **Busca**; ES para texto × PG para
detalhe em **Arquitetura**; queda de 20min do ES em **Sincronização** (outbox + drain);
Azure Pipelines em **CI/CD** e `docs/pipeline-evidence.md`; janela de inconsistência em
**Sincronização**; simplificações do MVP em **Limitações**.
