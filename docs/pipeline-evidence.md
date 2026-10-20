# Pipeline Evidence — Azure DevOps

Este documento é o entregável documental de CI/CD (seção 13 do desafio). O repositório
fica no **GitHub**, então o pipeline Azure DevOps (`azure-pipelines.yml`) é **lido como
código** e não executa neste host — a execução acontece ao importar o repo no Azure
DevOps (ou espelhar via Azure Pipelines com conexão GitHub).

## Visão geral dos stages

| Stage | Conteúdo | Falha bloqueia deploy? |
|---|---|---|
| **Build** | Restore + build + **testes de unidade** (backend, via template reutilizável `pipelines/templates/dotnet-build-test.yml`) e build do frontend (`tsc` estrito + `vite`) | Sim — Test depende de Build |
| **Test** | Testes de **integração black-box**: sobe a stack inteira com `docker compose up` e roda a suíte de contrato contra `http://localhost:8080` | Sim — Package depende de Test |
| **Package** | `dotnet publish` + `docker build/push` da imagem para o ACR com tag **imutável** `$(Build.BuildId)` + artefato versionado (`PublishBuildArtifacts`) com arquivo `VERSION.txt` | — (é o gate) |
| **DeployStaging** | `deployment` job no environment `techjobs-staging` + smoke test de `/health` | Sim — produção depende de staging |
| **DeployProduction** | `deployment` job no environment `techjobs-prod` — **pausa na aprovação** | — |

### Por que teste falho impede o deploy (evidence)

A cadeia é declarativa: `DeployProduction → dependsOn DeployStaging → dependsOn Package
→ dependsOn Test → dependsOn Build`. Um teste com exit code ≠ 0 falha o job; job falho
falha o stage; stage falho impede todos os stages que dependem dele. Logo **nenhum
artefato é publicado e nenhuma imagem é enviada ao registry** quando qualquer teste
falha. A evidência executável está no histórico de runs do pipeline: o stage `Test`
aparece vermelho e os stages seguintes ficam cinza ("Skipped"/"Canceled"), sem run de
deployment associado.

## Onde fica a aprovação de produção

**Não é declarável em YAML.** A aprovação é um *check* configurado na UI do Azure DevOps:

1. **Pipelines → Environments → `techjobs-prod`** (criado automaticamente na primeira
   referência no YAML, ou manualmente antes).
2. **Approvals and checks → Approvals → +**.
3. Definir os aprovadores (usuários/grupos — ex.: `techjobs-oncall`, líder técnico) e o
   timeout de aprovação (ex.: 4h; expirado ⇒ run falha, sem deploy).

Com esse check, o `deployment: Production` fica **aguardando aprovação** antes de
executar qualquer step — quem aprova vê o número do build, a imagem (`BuildId`) e o
resultado dos stages anteriores. Checks complementares recomendados no mesmo
environment: *Invoke Azure Function/REST* (validação externa), *Business hours* (janela
de deploy) e *Exclusive lock* (não rodar dois deploys em paralelo).

O YAML só **referencia** o environment (`environment: 'techjobs-prod'`); a política de
aprovação vive no objeto Environment do Azure DevOps — é exatamente por isso que a
aprovação não pode ser versionada/burlada por um PR.

## Rollback / redeploy da versão anterior

Duas rotas, da mais rápida para a mais controlada:

1. **Rerun de um run anterior saudável** — Runs → selecionar o run verde anterior →
   **Rerun** (ou "Rerun failed jobs"). Como cada run empacota a própria imagem
   (`techjobs-api:<BuildId>`), o rerun repõe exatamente a versão anterior.
2. **Redeploy do artefato** — Runs → run verde anterior → stage `DeployProduction` →
   **Redeploy release/stage**. O Azure DevOps rebaixa o mesmo artefato imutável,
   sem re-build nem re-teste (o artefato já passou pelos gates).

Em Kubernetes/AKS, o equivalente de emergência é apontar o deployment de volta à tag
da imagem anterior (`kubectl rollout undo deployment/techjobs-api`), pois a tag antiga
continua no ACR — **nunca** sobrescreva uma tag existente; o pipeline sempre gera tags
novas por `BuildId`.

## Onde vivem os segredos

Nada de segredo em texto claro no repositório (o `docker-compose.yml` tem credenciais
de DEV deliberadamente embutidas e sem valor; em produção o fluxo é outro):

- **Variable groups secretos**: Pipelines → Library → Variable groups → vincular ao
  **Azure Key Vault** (melhor prática: o grupo só referencia o cofre; segredos ficam no
  Key Vault com rotação e RBAC próprios). O pipeline referencia `group: techjobs-common`
  apenas para variáveis não-secretas (URLs, nomes de registry).
- **Service connections**: credenciais do ACR/subscription/Kubernetes ficam na service
  connection (`Project settings → Service connections`), com workload identity
  federada quando suportada — o YAML só usa o nome da conexão.
- **Variáveis secretas de pipeline** (`##vso[task.setsecret]` ou Library com lock) só
  para valores sem Key Vault, sempre marcadas como *secret* (mascaradas nos logs).

## Validação de PR: GitHub × Azure Repos

O YAML declara `pr: none` porque o repositório **está no GitHub**, onde a validação de
PR é feita pelo workflow `.github/workflows/ci.yml` (sobe a stack, roda os testes).
Se o código estivesse no **Azure Repos Git**, o gatilho `pr:` do YAML **não vale**:
lá a validação de PR é configurada por **Branch Policies** no branch `main`
(Repos → Branches → main → Branch Policies → **Build validation**, apontando este
pipeline como obrigatório) — sem isso, o merge não é bloqueado por build quebrado.
Essa é a diferença essencial: no GitHub o gatilho de PR é parte do workflow; no Azure
Repos é uma política do repositório.

## Executando localmente o que o pipeline faz

```bash
dotnet test backend/TechJobs.slnx --filter "FullyQualifiedName~UnitTests"   # stage Build
docker compose up -d --build                                               # stage Test
TECHJOBS_API=http://localhost:8080 dotnet test backend/TechJobs.slnx \
  --filter "FullyQualifiedName~IntegrationTests"                           # stage Test
dotnet publish backend/src/TechJobs.Api/TechJobs.Api.csproj -c Release     # stage Package
```
