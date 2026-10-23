# Proposta de valor adicional — TechJobs

**Data:** 22/10/2026  
**Status:** implementado em 22/10/2026 — itens P0.1–P0.5, P1.1–P1.3 e P2 aplicados e verificados (62 unit / 44 integração / carga medida em docs/performance.md).

## 1. Parecer

**Sim, existe espaço para entregar mais valor sem quebrar as regras.** O melhor investimento não é adicionar funcionalidades de produto: é demonstrar correção sob concorrência, resiliência verificável e facilidade de avaliação.

O enunciado permite reorganização interna, bibliotecas adicionais e diferenciais de cache, observabilidade e carga (seções 1, 17 e 20). Entretanto, diferenciais não compensam falhas nos requisitos principais. Elasticsearch, Redis e modelagem/API concentram 60% da nota; frontend representa 5%.

**Recomendação:** primeiro fechar riscos de contrato e consistência; depois acrescentar testes de carga e roteiro demonstrável. Refinamentos pequenos de interface ficam por último. Não recomendo uma nova arquitetura ou novos serviços de infraestrutura.

## 2. Base da avaliação e limites

Foram examinados o [enunciado](DESAFIO-SPEC.md), README, ADR, persistência, outbox/worker, cache, consultas, endpoints administrativos, tratamento de erros, frontend, testes e pipelines.

Validação local realizada:

| Verificação | Resultado |
| --- | --- |
| `dotnet test backend/TechJobs.slnx --filter "FullyQualifiedName~UnitTests" --no-restore` | 53 testes passaram; nenhum unitário ignorado |
| `npm run build`, em `frontend/` | TypeScript e build de produção passaram |
| `docker compose ps` | Sem serviços da stack em execução |
| Integração, concorrência, carga e navegação no navegador | Não executadas nesta análise |

O build .NET emitiu **NU1903** para `Microsoft.OpenApi 2.4.1`, com referência a [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc). O comentário no `.csproj` afirma que o pin evita versão vulnerável, mas o diagnóstico atual contradiz essa afirmação. É necessário verificar a versão corrigida compatível e repetir auditoria; não basta suprimir o aviso. O alerta não demonstra, por si só, exploração da aplicação.

Os riscos abaixo foram identificados por leitura do código. Os interleavings e casos-limite ainda precisam de testes de reprodução; os testes unitários verdes não comprovam conformidade integral com a suíte externa.

### O que já entrega valor

- Slices por caso de uso, DTOs e decisões documentadas, sem arquitetura excessiva.
- Escrita da vaga e pendência de indexação na mesma transação PostgreSQL.
- Cache-aside, TTL configurável, chave canônica de busca e degradação do Redis.
- Mapping explícito, título com boost, filtros, agregações e IDs estáveis na indexação.
- Endpoints determinísticos de avaliação e suíte black-box própria.
- Frontend com fluxos essenciais e pipelines com cadeia de dependências entre testes e deploy.

Esses elementos devem ser preservados, não reescritos para adicionar diferenciais.

## 3. Restrições inegociáveis

1. Preservar rotas públicas e `/admin/*`, portas 8080/3000, nomes de campos, enums, códigos HTTP e shapes das seções 6 e 7.
2. Manter `docker compose up --build` suficiente para inicialização, sem migração manual ou serviço adicional obrigatório.
3. Manter PostgreSQL como fonte oficial; Elasticsearch para busca e estatísticas; Redis como cache dispensável.
4. Preservar falhas simuladas: Redis não derruba leituras; falha simulada do Elasticsearch produz 503 na busca e não impede POST/PUT.
5. Manter seed/reset/drain/reindex síncronos conforme contrato; `/admin/*` deve responder 404 sem a flag.
6. Não introduzir autenticação, rate limiting ou novos parâmetros obrigatórios que impeçam a suíte de avaliação.
7. Não executar seed/reset ou carga contra ambiente compartilhado: usar stack descartável e autorização explícita para substituição de dados.

## 4. Prioridade zero: fortalecer o que já é obrigatório

### P0.1 — Impedir perda de atualização na outbox

**Evidência:** `backend/src/TechJobs.Api/JobsCore/Persistence/Outbox.cs` mantém uma linha por `job_id`; o enqueue atualiza `queued_at`, mas a remoção usa somente o ID. `Platform/Elastic/IndexingWorker.cs` lê IDs, lê vagas, indexa e remove pendências em operações separadas.

Interleaving possível:

1. Worker lê a versão A de uma vaga.
2. PUT confirma versão B e atualiza a pendência da mesma vaga.
3. Worker indexa A e remove a linha pelo ID, apagando também o aviso de B.
4. Sem outra escrita ou reindexação, B pode não chegar ao índice.

Além disso, worker e drain podem chamar `ProcessPendingAsync` simultaneamente. `Operations/Reindex.cs` captura a base e depois executa `ClearOutboxAsync()`: uma escrita durante a reconstrução pode ter sua pendência apagada sem estar no snapshot.

**Proposta:** identificar a pendência com revisão monotônica ou evento imutável e confirmar exclusivamente o trabalho consumido. Na arquitetura de instância única, serializar consumidores de indexação e coordenar reindexação com escritas/worker; remover a limpeza indiscriminada de eventos posteriores ao snapshot. Apenas colocar lock entre consumidores não resolve enqueue concorrente com remoção.

Qualquer mudança de schema deve atualizar bases existentes automaticamente; `CREATE TABLE IF NOT EXISTS` sozinho não adiciona colunas.

**Aceite:** testes com barreiras controladas entre leitura, indexação e confirmação; PUT concorrente não desaparece; drain termina com todas as escritas anteriores visíveis; reindexação concorrente não perde alterações; reexecução não duplica documentos.

**Valor:** protege sincronização e concorrência explicitamente avaliadas, sem broker ou microserviço novo.

### P0.2 — Validar semântica E entre título e descrição

**Evidência:** `Slices/SearchJobs/SearchQueryRunner.cs` usa `multi_match` com `operator: and`, sem explicitar tipo. O comportamento padrão é orientado a campos: pode exigir todos os termos no mesmo campo, em vez de aceitar termos distribuídos pelo documento. O teste existente contém os dois termos juntos e não isola esse caso.

**Proposta:** criar teste com `kubernetes` somente no título e `observabilidade` somente na descrição; ajustar a consulta para garantir todos os termos no conjunto dos dois campos, preservando boost do título e analyzer `portuguese`. Escolher implementação após validar relevância, não trocar o analyzer exigido.

**Aceite:** `q=kubernetes observabilidade` encontra a vaga; documento com só um termo não aparece; título continua pesando mais; case-insensitivity, stemming e acentuação têm regressões cobertas.

**Valor:** corrige possível desvio de contrato em busca, critério de maior peso.

### P0.3 — Tratar paginação além da janela padrão

**Evidência:** a consulta usa `from/size`, com `(page - 1) * pageSize` em `int`. `ElasticIndex.cs` não configura `max_result_window`. O seed aceita 20 mil vagas, enquanto a janela padrão do Elasticsearch é 10 mil resultados. Uma página válida pode produzir erro tratado como 503; valores extremos também podem causar overflow no offset.

**Proposta:** calcular offset com aritmética segura; testar página após o fim e navegação acima de 10 mil resultados. Para este protótipo, avaliar janela configurada para o volume previsto e retorno vazio com `total` correto quando a página exceder o total, consultando o Elasticsearch. Documentar o limite operacional real. Não impor teto de `page` ausente do contrato nem trocar paginação pública por cursor.

**Aceite:** seed de 20 mil permite consultar páginas válidas além de 10 mil; página muito além do fim retorna 200, `items: []` e `total` correto; números extremos não viram 500/503 por overflow.

### P0.4 — Alinhar precisão salarial entre escrita, detalhe e índice

**Evidência:** `Platform/Database.cs` persiste `numeric(14,2)`, mas `JobValidator.cs` aceita `decimal` sem política de escala/limite compatível. `JobStore.cs` monta a resposta da escrita a partir do objeto recebido, sem reler o valor persistido. Assim, um valor com mais de duas casas pode aparecer na resposta e ser arredondado pelo PostgreSQL no GET; valores grandes podem exceder a coluna. No Elasticsearch, salários estão como `double`, enquanto a seção 10 pede escala definida ou centavos.

**Proposta:** definir representação salarial coerente e documentada; verificar valores fracionários, grandes e bordas inclusivas. Avaliar mapping com escala explícita ou centavos e compatibilizar persistência/resposta. Como o enunciado não limita casas decimais nem magnitude, não adicionar rejeições arbitrárias silenciosamente: escolher preservação ou normalização justificada e explicitar eventual ambiguidade para avaliação.

**Aceite:** POST/PUT e GET representam o mesmo valor oficial; repetição do PUT continua idempotente; filtros inclusivos não perdem centavos; mapping satisfaz a exigência de escala; mudanças no índice/schema funcionam sem passos manuais.

### P0.5 — Higiene de segurança e evidência honesta de CI/CD

**Evidências:**

- Alerta NU1903 observado durante execução, inclusive com pin explícito em `TechJobs.Api.csproj`.
- `Contracts/GlobalExceptionHandler.cs` registra `detail`; `JobValidator.cs` inclui trechos de título/descrição nesse texto. Isso permite que conteúdo enviado pelo usuário chegue aos logs, contrariando a intenção de não registrar payload sensível.
- `azure-pipelines.yml` faz `docker push` sem etapa explícita de autenticação no registry; citar service connection em comentário não autentica o comando.
- Deploys atuais imprimem comandos com `echo`: são placeholders, não execução de implantação.
- `docs/pipeline-evidence.md` descreve rollback com rerun como se garantisse reutilização exata do artefato; reexecutar empacotamento pode reconstruir a imagem. `VERSION.txt` também referencia `BUILD_SOURCEVERSIONSUBSTRING`, variável que precisa ser definida ou substituída por SHA derivado de variável existente.

**Proposta:** atualizar dependência para versão corrigida compatível; retirar conteúdo de entrada dos logs, preservando Problem Details legível; alinhar YAML e documentação, identificando placeholders e pré-requisitos externos. Se houver build/push real no exemplo, incluir autenticação por service connection. Descrever rollback por reutilização do artefato/digest anterior sem reconstrução e sem prometer execução Azure que não ocorreu.

**Aceite:** auditoria não acusa o advisory; teste de logging não encontra trecho sensível enviado na validação; pipeline permanece documental, porém internamente coerente; cadeia de testes continua bloqueando package/deploy; documentação distingue execução comprovada de configuração proposta.

## 5. Diferenciais recomendados após P0

### P1.1 — Single-flight com prova de concorrência

**Base:** seção 17 permite mitigação de cache stampede. `GetJobByIdEndpoint.cs` e `SearchJobsEndpoint.cs` deixam misses concorrentes alcançarem a fonte.

**Proposta:** coalescer preenchimentos por chave, inicialmente dentro da instância da API, com segunda consulta ao cache após adquirir coordenação. Aplicar a detalhe e, se o esforço permitir, busca. Não usar um lock global nem manter um dicionário de locks que cresce indefinidamente.

**Cuidado essencial:** single-flight entre GETs **não elimina**, sozinho, a corrida GET/PUT já documentada no README. Para mitigar essa corrida na instância única, coordenar também a escrita/invalidação por vaga ou adotar protocolo de revisão. Tratar separadamente o caso PUT durante falha do Redis seguido de recuperação, pois a chave antiga pode sobreviver. Não anunciar consistência forte nem suporte a múltiplas réplicas sem mecanismo correspondente.

**Aceite:** rajada para a mesma chave reduz leituras à fonte, comprovada por contador de teste ou instrumentação; chaves distintas seguem concorrentes; timeout/cancelamento não prende seguidores; Redis indisponível preserva fallback e semântica dos contadores. Teste de barreira comprova a garantia de invalidação efetivamente escolhida.

### P1.2 — Carga reproduzível e roteiro técnico curto

**Proposta de novos entregáveis:**

- `tests/load/`: cenários k6 ou equivalente, opcionais e separados da subida padrão.
- `docs/performance.md`: comando, ambiente, commit, volume, concorrência e resultados reais.
- `docs/demo.md`: roteiro de 5–10 minutos para avaliador.

**Cenários:** detalhe com cache frio/quente; consultas canonicamente equivalentes; chaves distintas; rajada após expiração; Redis indisponível; escrita durante falha do Elasticsearch e recuperação após drain. Separar carga saudável de falhas esperadas: 503 simulado não deve contaminar taxa de erro do cenário normal.

**Métricas:** p50/p95/p99, throughput, erros inesperados, deltas de hit/miss/error e latência de convergência. Contadores de cache não substituem contagem de chamadas ao PostgreSQL/Elasticsearch; instrumentar a fonte para provar redução. Não exigir ganho artificial de latência em execução local ruidosa nem inventar números.

**Roteiro:** seed determinístico → busca com filtros → miss/hit → PUT e detalhe atualizado → encerramento + drain → Redis em falha sem 500 → Elasticsearch em falha com escrita preservada → recuperação + estatísticas → reindex duas vezes sem duplicação.

**Aceite:** execução por comando documentado em stack descartável; falhas simuladas desativadas ao final; resultados identificam se foram medidos ou permanecem pendentes. Usar inicialmente como evidência manual ou workflow opcional, não como gate de latência frágil em todo PR.

**Valor:** torna os diferenciais auditáveis usando mecanismos já exigidos pelo desafio.

### P1.3 — Evitar CI verde com integração ignorada

**Evidência:** `backend/tests/TechJobs.IntegrationTests/Infrastructure.cs` marca testes como skip quando `/health` não responde. Há espera pela API nos pipelines, mas uma queda posterior pode fazer testes serem ignorados e ainda produzir execução verde.

**Proposta:** manter skip conveniente para uso local, porém exigir modo estrito em CI: indisponibilidade falha a suíte; publicar TRX e verificar que a suíte esperada executou, sem skips inesperados. Evitar prender o gate a um número fixo que precise ser ajustado a cada teste novo.

**Aceite:** parar a API antes da descoberta da suíte, em cenário controlado de CI, torna job vermelho; integração saudável produz relatório; validar também `/admin/*` indisponível com flag desligada, expiração real com TTL curto e POST **e PUT** durante falha do Elasticsearch.

## 6. Melhorias pequenas de experiência — somente com folga

**Evidência:** `frontend/src/App.tsx` troca telas desmontando a busca; filtros/página retornam aos padrões ao voltar do detalhe. `components/ui.tsx` abre vaga via `article` com `onClick`, sem controle nativo de teclado.

**Proposta enxuta:**

- Preservar filtros e página ao voltar; opcionalmente serializá-los na URL do frontend, sem mudar rotas da API.
- Usar link/botão acessível para abrir vaga; garantir foco visível e anunciar carregamento.
- Após cadastro, abrir detalhe retornado pelo POST ou informar atraso possível da busca, em vez de induzir a impressão de cadastro perdido.
- Validar salário de forma explícita no formulário: hoje `Number()` pode gerar `NaN`, serializado como `null`, convertendo entrada inválida em ausência de salário.
- Acrescentar um teste ponta a ponta: buscar → detalhe → editar/encerrar → voltar preservando filtros.

**Aceite:** fluxo funciona por teclado; salário inválido não vira ausência silenciosa; navegação preserva contexto; nenhum investimento em identidade visual ou nova API.

## 7. O que adiar

| Ideia | Motivo |
| --- | --- |
| Login, candidaturas, favoritos persistidos, notificações e uploads | Domínio novo, fora do objetivo principal |
| Broker, microserviços, Kubernetes ou cluster de observabilidade obrigatório | Complexidade e custo de execução sem retorno proporcional |
| Reindexação gradual com aliases | Permitida, mas coordenar a reconstrução atual vem primeiro; preservar compatibilidade com `techjobs-v1` |
| Outbox com dead-letter e backoff persistente completos | Permitido; após garantir que não há perda de eventos. Retry fixo de 1s não é backoff exponencial |
| OpenTelemetry/Prometheus completos | Opcionais; começar por instrumentação mínima usada nos testes, sem alterar shapes administrativos |
| Redesign visual amplo | Frontend vale 5%; usabilidade funcional tem retorno melhor |

## 8. Ordem de entrega e critério de parada

1. **Correção:** reproduzir e tratar P0.1–P0.4 com testes de regressão. Priorizar outbox e semântica textual; não promover diferencial enquanto houver quebra conhecida do contrato.
2. **Confiança:** resolver alerta de dependência/logging e alinhar evidência Azure; endurecer execução da integração em CI.
3. **Diferencial focalizado:** single-flight com limites explícitos e testes determinísticos.
4. **Demonstração:** carga reproduzível, resultados reais e roteiro técnico.
5. **Acabamento opcional:** preservar contexto e acessibilidade do frontend.

Cada incremento deve ser pequeno e reversível, mantendo unitários, build frontend e integração verdes. Testes com reset/seed/faults devem executar isolados da carga e de outras suítes que compartilhem a base.

**Pacote final de maior valor:** contrato intacto + sincronização sem perda conhecida + cache sob concorrência comprovado + CI sem falso positivo + demonstração reproduzível. Se o prazo apertar, entregar correções e evidências; deixar opcionais explicitamente documentados. Isso segue a orientação do enunciado: solução menor, funcional e justificada vale mais que escopo extenso incompleto.
