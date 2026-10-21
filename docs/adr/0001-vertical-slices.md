# ADR-0001: Arquitetura em Vertical Slices

**Status:** Aceito

**Data:** 2026-10-21

## Contexto

A TechJobs é uma API que orquestra três infraestruturas com papéis distintos:
PostgreSQL (fonte da verdade transacional), Redis (cache de leitura) e
Elasticsearch (projeção de busca). No desenho original, a classe `JobsService`
concentrava os cinco casos de uso do domínio (criar, atualizar, detalhar, buscar
e agregar estatísticas de vagas), acumulando dependências distintas para cada
fluxo — injeções que nem todo método usava.

A complexidade do projeto é assimétrica por caso de uso, apesar de haver um
único agregado de domínio (`Job`):

- **Leitura de detalhe** (`GET /api/jobs/{id}`): cache-aside no Redis com
  fallback no PostgreSQL.
- **Busca** (`GET /api/jobs/search`, `/api/jobs/stats`): consulta ao
  Elasticsearch com cache por chave canônica.
- **Escrita** (`POST`/`PUT /api/jobs`): commit no PostgreSQL com evento de
  outbox na mesma transação, indexação eventual no Elasticsearch.

Um serviço central único esconde essa assimetria: cada caso de uso tem forma
própria de acesso a dados, semântica de cache e estratégia de degradação.

## Decisão

Adotar **Vertical Slice com kernel fino**:

- **1 rota pública = 1 slice**: cada caso de uso vive em uma pasta própria
  (`Slices/CreateJob`, `Slices/UpdateJob`, `Slices/GetJobById`,
  `Slices/SearchJobs`, `Slices/JobStats`) com o endpoint e o handler do fluxo.
- **Handlers simples, sem MediatR**: o fluxo é legível de ponta a ponta no
  arquivo do slice, sem indireção de pipeline/mensageria.
- **`Contracts/` congelado pelo contrato de API**: DTOs, exceções de contrato e
  o handler global de Problem Details são a fronteira física que preserva rotas,
  shapes de JSON e semânticas de falha.
- **`Operations/` separada e dev-only**: os endpoints `/admin/*` (reset, seed,
  faults, cache-stats, drain, reindex) existem apenas com
  `ENABLE_TEST_ENDPOINTS=true` e não fazem parte do contrato público.
- **`Platform/` para o transversal**: indexação/outbox (`Platform/Elastic`),
  cache Redis e falhas simuladas (`Platform/Runtime`), inicialização de
  infraestrutura e acesso ao PostgreSQL.
- **Suíte black-box como rede de segurança do contrato**: 26 testes de
  integração contra a stack viva, com gate duplo por fase (53 testes de unidade
  + 26 de integração) garantindo que a reorganização não alterou o comportamento
  observável.

## Alternativas consideradas

- **Feature-Based** (pastas por feature sobre serviços centrais): melhor que
  camadas horizontais, mas preservaria serviços centrais (`JobsService`) e
  fronteiras convencionais entre feature e serviço — a dependência por fluxo
  continuaria diluída no serviço.
- **Modular Monolith**: com um único bounded context (`Job`), a cerimônia da
  abordagem (módulos, contratos internos entre módulos, event bus) não traria
  ganho proporcional. O outbox já é o seam natural caso a indexação venha a se
  tornar um serviço separado no futuro.

## Consequências

**Positivas**

- Coesão por caso de uso: tudo que um fluxo precisa (endpoint, handler,
  dependências) vive no slice, e nada do que ele não precisa é injetado.
- A semântica de degradação de cada fluxo fica visível no slice que a possui
  (cache-aside no GetJobById, cache canônico no SearchJobs, escrita + outbox nos
  slices de escrita).
- O contrato de API vira fronteira física (`Contracts/`), protegido pela suíte
  black-box.
- Migração strangler segura: extrair um slice para outro deployável não exige
  desmontar um serviço central.

**Negativas**

- O kernel (`Platform/`, `Contracts/`) exige disciplina para não inchar e
  absorver lógica que pertence aos slices.
- Pequena duplicação intencional de helpers entre slices (ex.: leitura e
  validação do corpo da requisição nos slices de escrita), preferida à criação
  prematura de abstrações compartilhadas.

**Neutras**

- Continua um único `csproj` e um único deployável — o `docker compose` e o
  pipeline permanecem inalterados.
