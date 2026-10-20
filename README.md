# Desafio Técnico C# Pleno/Sênior — Catálogo de Vagas com Busca (Elasticsearch) e Cache (Redis)

Bem-vindo(a)! Este repositório é o ponto de partida do desafio. Ele já contém:

- `backend/` — esqueleto de uma Web API .NET 10 (apenas `/health` implementado), com `Npgsql`, `StackExchange.Redis` e `Elastic.Clients.Elasticsearch` já referenciados no `.csproj`;
- `frontend/` — esqueleto React 18 + TypeScript + Vite;
- `docker-compose.yml` — sobe API, **PostgreSQL**, **Redis**, **Elasticsearch** e frontend.

Você pode reorganizar a estrutura interna como preferir (camadas, projetos, pastas) e trocar bibliotecas de acesso a dados, desde que o **Contrato de API** (seção 6) e o `docker compose up` continuem funcionando.

---

## 1. Contexto

Uma empresa mantém um catálogo de vagas de tecnologia. Usuários precisam encontrar oportunidades por título, descrição, tecnologias, senioridade, modalidade e salário. O volume de consultas de leitura supera com folga o volume de cadastros, e o catálogo muda durante o dia.

Três componentes desempenham papéis diferentes e precisam cooperar sem entrar em conflito:

- **PostgreSQL** é o cartório: a fonte oficial e transacional da vaga. Nenhuma leitura de detalhe pode contradizer o que está lá.
- **Elasticsearch** é o catálogo de biblioteca: uma cópia indexada, otimizada para busca textual, filtros e agregações. Nunca é a fonte da verdade e pode ficar defasado por um intervalo curto.
- **Redis** é o bloco de anotações da recepção: respostas de leitura muito consultadas, com expiração — que nunca substitui persistência durável.

O desafio é mostrar onde eles cooperam (cache-aside, projeção de busca) e onde podem conflitar (dado desatualizado em cache, índice atrasado, dependência indisponível) — e deixar essas decisões observáveis e documentadas.

---

## 2. Prazo

Prazo sugerido: até 7 dias corridos. Não é necessário um produto pronto para produção — esperamos um protótipo funcional que demonstre capacidade de modelar o domínio, orquestrar três infraestruturas distintas e justificar decisões de consistência e cache.

---

## 3. Tecnologias obrigatórias

**Backend**: C#; ASP.NET Core com .NET 10; **PostgreSQL** (fonte da verdade); **Redis** (cache); **Elasticsearch** (busca); API REST; documentação OpenAPI/Swagger (recomendado, ver seção 15); injeção de dependência; DTOs nas fronteiras da API. Acesso a dados é sua escolha (EF Core, Dapper, ADO puro); os clientes oficiais de Redis e Elasticsearch já estão referenciados no `.csproj`, mas você pode acessar via HTTP/protocolo nativo se preferir.

**Frontend**: React 18; componentização adequada; separação entre páginas, componentes, serviços e tipos — não concentrar tudo em `App.tsx`.

Backend, cache e busca são a parte principal da avaliação (60% da nota juntos — seção 14). A interface deve permitir usar os fluxos essenciais, mas não é avaliada por aspectos visuais.

---

## 4. Modelo de dados — entidade `Job`

| Campo | Tipo lógico | Regra |
|---|---|---|
| `id` | UUID | Gerado pelo servidor |
| `title` | string | Obrigatório, 5 a 160 caracteres |
| `company` | string | Obrigatório, 1 a 120 caracteres |
| `description` | string | Obrigatório, no mínimo 30 caracteres |
| `technologies` | string[] | Uma ou mais; sem duplicatas após normalização |
| `seniority` | enum | `junior` \| `pleno` \| `senior` |
| `workMode` | enum | `remoto` \| `hibrido` \| `presencial` |
| `city` | string/null | Opcional |
| `salaryMin` | decimal/null | Opcional; não negativo; se informado junto com `salaryMax`, `salaryMax >= salaryMin` |
| `salaryMax` | decimal/null | Opcional; não negativo |
| `status` | enum | `aberta` \| `encerrada` |
| `publishedAt` | UTC datetime | Preenchido pelo servidor no POST |
| `updatedAt` | UTC datetime | Atualizado a cada alteração |

**Regra de domínio**: apenas vagas `aberta` aparecem em `/api/jobs/search` e `/api/jobs/stats`. A consulta por ID **pode** retornar vaga `encerrada` — essa é a decisão esperada (o detalhe mostra o estado oficial, mesmo encerrado).

Comparação de tecnologias (filtro, busca e normalização) é **case-insensitive** após trim: `"redis"` e `"Redis"` são a mesma tecnologia.

---

## 5. Escopo funcional

1. **Cadastro** (`POST`) e **atualização** (`PUT`) de vagas, incluindo encerrar uma vaga via `PUT` com `status: "encerrada"`.
2. **Detalhe por ID** (`GET`) acelerado por cache-aside no Redis.
3. **Busca pública** (`GET /api/jobs/search`) com texto livre, filtros combinados, ordenação e paginação, servida pelo Elasticsearch e cacheada no Redis.
4. **Estatísticas** (`GET /api/jobs/stats`) das vagas abertas, derivadas de agregações do índice de busca — nunca números fixos.
5. **Seed determinístico** de vagas sintéticas (via `POST /admin/seed`, seção 7) — títulos e descrições plausíveis em português, mistura de tecnologias, distribuição variada de senioridade/modalidade, faixas salariais coerentes, parte das vagas sem salário e parte encerradas.

---

## 6. CONTRATO DE API (obrigatório — não altere rotas, portas nem nomes de campos)

A avaliação é feita por uma suíte automatizada que consome exatamente este contrato. Divergências de rota, porta ou shape de JSON reprovam os testes automatizados.

- A API deve escutar em **`http://localhost:8080`** (via `docker compose up`).
- O frontend deve escutar em **`http://localhost:3000`**.
- JSON em **camelCase**; enums como **string**; datas em ISO 8601 (UTC).
- A API deve habilitar **CORS** para o frontend.
- Erros seguem **Problem Details** (RFC 9457), com pelo menos `type`, `title`, `status`, `detail`:

```json
{
  "type": "https://.../techjobs/validation-error",
  "title": "Validation failed",
  "status": 400,
  "detail": "salaryMax (5000) deve ser maior ou igual a salaryMin (6500)."
}
```

Códigos HTTP: **400** payload/filtro inválido (com `detail` legível); **404** vaga não encontrada; **503** busca indisponível (Elasticsearch fora do ar, ver seção 7); **500** erro inesperado.

### Rotas públicas

```http
POST /api/jobs
PUT  /api/jobs/{id}
GET  /api/jobs/{id}
GET  /api/jobs/search
GET  /api/jobs/stats
```

- **`POST /api/jobs`** — corpo:

```json
{
  "title": "Desenvolvedor(a) Backend .NET Pleno",
  "company": "Acme Tech",
  "description": "Desenvolvimento de APIs e servicos com C#, PostgreSQL, Redis e testes automatizados. Trabalho colaborativo com revisao de codigo.",
  "technologies": ["C#", ".NET", "Redis", "PostgreSQL"],
  "seniority": "pleno",
  "workMode": "remoto",
  "city": null,
  "salaryMin": 6500,
  "salaryMax": 8500,
  "status": "aberta"
}
```

**201 Created** com a vaga completa (`id`, `publishedAt`, `updatedAt` preenchidos pelo servidor) e header `Location` apontando para `GET /api/jobs/{id}`. Campos inválidos → **400**.

- **`PUT /api/jobs/{id}`** — mesmo corpo do POST (substituição completa). **200** com a vaga atualizada. **404** se não existe; **400** se inválido. Deve ser **idempotente**: o mesmo payload aplicado duas vezes deixa a vaga no mesmo estado. A atualização é confirmada contra o **PostgreSQL** — a indexação no Elasticsearch pode ficar pendente por um instante (seção 8). Após o commit, o cache de detalhe da vaga deve ser invalidado.

- **`GET /api/jobs/{id}`** — **200** com a vaga completa, inclusive `encerrada`. **404** se não existe. Leitura cache-aside: 1ª leitura preenche o cache (TTL configurável), seguintes leem do cache até expirar ou a vaga ser alterada.

- **`GET /api/jobs/search`** — parâmetros de query:

| Parâmetro | Significado | Regra |
|---|---|---|
| `q` | Texto livre | Opcional; procurado em `title` e `description`, **título com peso maior** (peso exato é sua escolha, documente). Case-insensitive. Com múltiplos termos, **todos** os termos devem estar presentes no documento (semântica E). |
| `seniority` | Filtro exato | `junior` \| `pleno` \| `senior`; valor fora do enum → **400** |
| `workMode` | Filtro exato | `remoto` \| `hibrido` \| `presencial`; valor fora do enum → **400** |
| `technologies` | CSV de tecnologias | Semântica **TODAS**: a vaga deve conter todas as informadas (case-insensitive). Ex.: `technologies=Redis,C%23` |
| `minSalary` | Salário mínimo desejado | Vaga precisa de `salaryMax >= minSalary`. Vaga sem faixa salarial é **excluída** |
| `maxSalary` | Salário máximo procurado | Vaga precisa de `salaryMin <= maxSalary`. Vaga sem faixa salarial é **excluída** |
| `page` | Página, 1-based | Mínimo 1, senão **400** |
| `pageSize` | Itens por página | 1 a 50, padrão 10; fora da faixa → **400** |
| `sort` | Ordenação | `relevance` (padrão) \| `newest` \| `salary_desc`; sem `q`, `relevance` degrada para `newest` (documente) |

`minSalary`/`maxSalary` informados juntos usam **interseção de intervalos** (`salaryMax >= minSalary` **E** `salaryMin <= maxSalary`), ambos inclusivos. Salários são decimais não negativos; valor negativo → **400**.

Ordenações: `newest` = `publishedAt` descendente (empate: `id` ascendente); `salary_desc` = `salaryMax` descendente, vagas sem salário por último (empate: `id` ascendente); `relevance` = score do `q` (título pesa mais que descrição).

**200** com:

```json
{
  "page": 1,
  "pageSize": 10,
  "total": 127,
  "items": [
    {
      "id": "11111111-1111-4111-8111-111111111111",
      "title": "Desenvolvedor(a) Backend .NET Pleno",
      "company": "Acme Tech",
      "seniority": "pleno",
      "workMode": "remoto",
      "city": null,
      "technologies": ["C#", ".NET", "Redis"],
      "salaryMin": 6500,
      "salaryMax": 8500,
      "publishedAt": "2026-10-01T12:00:00Z"
    }
  ]
}
```

`total` é o total de matches da consulta (sem paginação); `items` pode vir vazio quando a página passa do fim. Apenas vagas `aberta` aparecem. Toda a consulta é servida pelo **Elasticsearch** e protegida por **cache de resultado no Redis** (seção 7).

- **`GET /api/jobs/stats`** — **200** com estatísticas **das vagas abertas**, derivadas de agregações da projeção de busca:

```json
{
  "totalOpenJobs": 4750,
  "bySeniority": { "junior": 1050, "pleno": 2460, "senior": 1240 },
  "byWorkMode": { "remoto": 2850, "hibrido": 1420, "presencial": 480 },
  "topTechnologies": [
    { "name": "C#", "count": 1800 },
    { "name": "Node.js", "count": 1450 }
  ]
}
```

`bySeniority`/`byWorkMode` devem somar `totalOpenJobs`; `topTechnologies` (mínimo 10 itens quando houver) é soma de ocorrências — a mesma vaga contribui para várias tecnologias, então a soma pode exceder `totalOpenJobs` (explique no README).

---

## 7. Endpoints dev-only — mecanismo de teste determinístico (`ENABLE_TEST_ENDPOINTS`)

A suíte de avaliação precisa forçar estados e falhas de forma determinística. Estes endpoints só precisam existir (e só devem existir) quando a variável de ambiente **`ENABLE_TEST_ENDPOINTS=true`** — que o `docker-compose.yml` deste template já seta. **Nunca** ative essa flag em produção real. Sem a flag, os endpoints respondem 404.

| Endpoint | Corpo | Comportamento |
|---|---|---|
| `POST /admin/reset` | vazio | **200**. Limpa o PostgreSQL, recria o índice vazio, limpa o Redis, zera contadores e desliga falhas simuladas. Síncrono. |
| `POST /admin/seed` | `{"count": 5000, "seed": 42}` (campos opcionais; `count` 1–20000, `seed` inteiro) | **200** `{"count": N, "seed": S}`. **Substitui** toda a base por vagas sintéticas **determinísticas**: mesmo `count`+`seed` produzem exatamente o mesmo conteúdo (títulos, empresas, descrições, techs, salários, status). Síncrono: ao responder, PostgreSQL e índice já refletem a base e os caches estão limpos. |
| `GET /admin/cache-stats` | — | **200** com contadores **desde o último reset**: `{"config": {"detailsTtlSeconds": N, "searchTtlSeconds": N}, "details": {"hit": N, "miss": N, "error": N}, "search": {"hit": N, "miss": N, "error": N}}`. `config` reflete a configuração ativa dos TTLs. `error` conta toda tentativa de uso do Redis que falhou (exceção real ou falha simulada). |
| `POST /admin/faults` | `{"target": "redis" \| "elasticsearch", "enabled": true}` | **200** `{"target": "...", "enabled": true/false}`. Liga/desliga uma **indisponibilidade simulada** (seção 9). |
| `POST /admin/indexing/drain` | vazio | **200** somente depois que **toda escrita confirmada** (POST/PUT feitos antes da chamada) estiver visível em `/api/jobs/search` e `/api/jobs/stats`. Na prática: aplica indexação pendente, faz refresh do índice e invalida o cache de busca. |
| `POST /admin/indexing/reindex` | vazio | **200** `{"count": N}`. Reconstrói o índice a partir do PostgreSQL **sem duplicar** documentos (mesmo executado duas vezes seguidas). |

---

## 8. Requisitos técnicos — Redis (cache)

- **Cache-aside obrigatório** em `GET /api/jobs/{id}`: consulta o Redis; em miss, lê o PostgreSQL, grava a resposta no cache com TTL e responde.
- Chave de detalhe sugerida: `job:details:v1:{id}` (versionada — evoluir o shape não pode colidir com cache antigo).
- **TTL de detalhe configurável** via `Cache__DetailsTtlSeconds` (padrão 300) — o valor ativo aparece em `/admin/cache-stats`.
- **Invalidação**: após o commit do `PUT`, a chave de detalhe da vaga é invalidada — o valor antigo nunca pode voltar indefinidamente.
- **Cache de busca obrigatório**: chave canônica derivada de uma representação normalizada de `q`, filtros, `page`, `pageSize` e `sort` (ex.: sha256 da query canônica) — duas consultas idênticas reutilizam a chave; filtros/ordenações/páginas diferentes **não** colidem. TTL via `Cache__SearchTtlSeconds` (padrão 60).
- **Degradação graciosa**: com o Redis indisponível (falha real ou simulada via `/admin/faults`), `GET /api/jobs/{id}` continua respondendo a partir do PostgreSQL e a busca continua respondendo a partir do Elasticsearch sem cache — nada pode retornar 500 por culpa do Redis.
- Não registrar strings de conexão nem conteúdo de payload sensível nos logs.

## 9. Requisitos técnicos — falhas simuladas

Com `{"target": "redis", "enabled": true}`:

- A API **não consegue** usar o Redis (trate como exceção). Cada tentativa incrementa o contador `error` correspondente e o fluxo segue em degradação: `GET /api/jobs/{id}` → 200 via PostgreSQL; `/api/jobs/search` → 200 via Elasticsearch sem cache.

Com `{"target": "elasticsearch", "enabled": true}`:

- `/api/jobs/search` → **503** Problem Details (erro controlado, sem stack trace).
- `POST /api/jobs` e `PUT /api/jobs/{id}` continuam **confirmando a escrita no PostgreSQL** (201/200) — a indexação falha ou fica pendente. É aceitável que a busca fique defasada; não é aceitável perder o cadastro oficial nem retornar 5xx na escrita.

Desligar a falha e chamar `/admin/indexing/drain` devolve o sistema ao estado consistente.

## 10. Requisitos técnicos — Elasticsearch e sincronização

- Índice `techjobs-v1` com **mapping explícito**: `title`/`description` como `text` com analyzer `portuguese`; `company` como `text` com subcampo `raw` keyword; `technologies`, `seniority`, `workMode`, `city`, `status` como `keyword`; salários como tipo numérico com escala definida (ou inteiros em centavos — documente); datas como `date`. O `id` do documento é o `id` da vaga (estável).
- Busca com `multi_match` (ou equivalente) em `title` (peso maior) e `description`; filtros exatos em `keyword`; exclusão de `encerrada`; sem `q`, usar consulta apropriada (`match_all` filtrada), nunca `multi_match` vazio.
- Agregações (`terms`) por senioridade, modalidade e tecnologias para `/api/jobs/stats`.
- **Sincronização eventualmente consistente**: a gravação ocorre primeiro no PostgreSQL; a indexação acontece depois da confirmação. Documente o que acontece se a aplicação cair entre o commit e a indexação, e como se recupera (rotina de reindexação — exercida por `/admin/indexing/reindex`).
- Analise no README o impacto do analyzer `portuguese` (stemming, acentuação) nas buscas.

## 11. Requisitos técnicos — PostgreSQL

Fonte da verdade transacional. Toda escrita (POST/PUT) é confirmada aqui antes de qualquer resposta de sucesso. `technologies` pode ficar em `jsonb`, `text[]` ou tabela normalizada — explique a escolha. Migrations ou schema inicial automático: escolha um caminho que funcione no `docker compose up` sem passos manuais.

## 12. Frontend

A aplicação React deve permitir: buscar vagas com texto livre e filtros (senioridade, modalidade, tecnologias, faixa salarial), paginar e reordenar resultados; abrir o detalhe de uma vaga; cadastrar e editar uma vaga (incluindo encerrar); ver as estatísticas. Tratamento de carregamento e erro (exibindo o Problem Details de forma amigável). Não é necessário investir em identidade visual.

## 13. CI/CD — Azure DevOps (entregável documental)

Este repositório fica no GitHub, então o pipeline Azure DevOps **não é executado aqui** — mas ele é parte da avaliação, lido como código. Entregue na raiz:

- **`azure-pipelines.yml`** com stages separados: `Build` (restore + build), `Test` (testes automatizados — falha de teste impede qualquer deploy), `Package` (artefato versionado/imutável), `DeployStaging` e `DeployProduction` (deployment jobs com `environment`).
- **Pelo menos um template YAML reutilizável** (ex.: `pipelines/templates/dotnet-build-test.yml`) referenciado pelo pipeline principal.
- **`docs/pipeline-evidence.md`** explicando: onde a aprovação de produção fica configurada (approval/check no **Environment** do Azure DevOps — deixe claro que não é algo declarável em YAML), como fazer rollback/redeploy da versão anterior, e onde os segredos viveriam (variable groups secretos / Key Vault / service connections — nada em texto claro no repositório).
- Nota: se o repo estivesse no Azure Repos Git, a validação de PR seria por **Branch Policies → Build validation** (o gatilho YAML `pr:` não vale lá). Mencione essa diferença na documentação.

## 14. Testes

A solução deve possuir testes automatizados. São esperados, no mínimo: teste de validação de cadastro (regras da seção 4); teste da semântica salarial (incluindo vagas sem salário); teste de normalização da chave de cache de busca (consultas idênticas reutilizam, diferentes não colidem); teste de hit/miss e invalidação após `PUT`; teste de filtros e agregações; teste de vaga encerrada saindo da busca; teste de idempotência do `PUT`; teste de reindexação sem duplicar.

## 15. Requisitos não funcionais

A aplicação deve: utilizar operações assíncronas; não armazenar credenciais no repositório; possuir configuração por ambiente (connection strings/URLs/TTLs fora do código); fornecer logs minimamente estruturados **sem strings de conexão**; ser executável localmente com `docker compose up --build`, sem passos manuais; expor Swagger/OpenAPI (recomendado — ajuda a demonstrar os contratos).

## 16. Entregáveis

Repositório contendo: código-fonte do backend e frontend; `azure-pipelines.yml` + template + `docs/pipeline-evidence.md`; testes automatizados; `README.md` com instruções de execução, decisões arquiteturais (cache e invalidação, sincronização banco↔índice, modelagem salarial/tecnologias), trade-offs e limitações conhecidas.

## 17. Diferenciais opcionais

Não são obrigatórios: transactional outbox (tabela `outbox_events` + worker com retry e backoff); mitigação de cache stampede (single-flight, lock distribuído ou outra estratégia justificada); reindexação gradual com aliases/versionamento de índices; métricas/traces (OpenTelemetry); teste de carga próprio (k6 ou similar); contadores exportados em formato Prometheus; frontend com experiência de busca refinada; ADRs.

Diferenciais não compensam problemas nos requisitos principais.

## 18. Critérios de avaliação

| Critério | Peso |
| --- | --- |
| Elasticsearch (mapping, relevância, filtros, agregações, sincronização) | 25% |
| Redis (cache-aside, TTL, invalidação, chaves canônicas, falhas) | 20% |
| Modelagem e API (dados válidos, contrato, semântica salarial) | 15% |
| Arquitetura e decisões técnicas | 15% |
| CI/CD Azure DevOps (pipeline, template, approval, rollback) | 10% |
| Testes automatizados | 10% |
| Frontend | 5% |

- **Testes (10%)** é 100% automático (suíte black-box + concorrência + carga).
- **CI/CD (10%)** é avaliado por leitura do `azure-pipelines.yml`, do template e da documentação de evidência.

## 19. Perguntas para apresentação técnica

1. Por que cache-aside é apropriado para o detalhe da vaga? O que acontece quando o TTL vence?
2. Qual a diferença entre expiração (TTL) e evicção (LRU/LFU)? Como mitigaria cache stampede numa vaga muito popular?
3. Como você evita retorno de uma vaga desatualizada após um `PUT`? Quando a invalidação pós-escrita ainda sofre corrida?
4. Qual a diferença entre `match` e `term`, e entre `text` e `keyword` no Elasticsearch?
5. Por que a busca por texto usa Elasticsearch e a consulta por ID usa PostgreSQL?
6. Se o Elasticsearch cair por 20 minutos, como o índice se recupera depois? O que acontece com as vagas cadastradas nesse período?
7. Diferencie stage, job, task e deployment job no Azure Pipelines. Por que usar templates?
8. Onde fica a aprovação de produção no Azure DevOps e como você prova que um teste falho impede o deploy?
9. Qual a janela de inconsistência entre um `PUT` confirmado e a busca refletir a mudança? Do que ela depende?
10. O que você simplificou neste MVP e o que mudaria antes de um lançamento público?

## 20. Observações finais

O desafio não é avaliado apenas pela quantidade de funcionalidades entregues. Uma solução menor, funcional, testada e bem justificada pode valer mais do que uma solução extensa e incompleta.

É permitido: simplificar partes da interface; documentar decisões que não puderam ser implementadas; usar bibliotecas adicionais. O contrato da seção 6 (e os endpoints dev-only da seção 7) são as únicas partes que não podem ser alteradas.

---

## Como executar

```bash
docker compose up --build
# API:          http://localhost:8080/health
# Frontend:     http://localhost:3000
# Elasticsearch: http://localhost:9200
# Swagger:      http://localhost:8080/swagger (se habilitado)
```

A stack sobe vazia. Popule a base com o seed determinístico (ex.: `POST /admin/seed` com `{"count": 5000, "seed": 42}`) — veja seção 7.

## CI incluída

O workflow `.github/workflows/ci.yml` roda a cada push: sobe a stack com `docker compose up` e verifica `GET /health` e o frontend. É o pré-requisito mínimo da entrega — mantenha-o verde. A avaliação final usa uma suíte automatizada adicional contra o contrato de API da seção 6.
