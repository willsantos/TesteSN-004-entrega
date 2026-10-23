using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Elastic;

namespace TechJobs.Api.Platform;

/// <summary>
/// Garante schema do PostgreSQL e índice do Elasticsearch no startup, com retry
/// (dependências sobem saudáveis pelo docker-compose, mas desempates não custam).
/// Quando o índice já existe com mapping antigo (salário em double), executa a
/// migração automática: reconstrução completa da projeção a partir do PostgreSQL
/// (drop + create com scaled_float), sob exclusão mútua com o worker de indexação —
/// padrão do reindex administrativo, via IndexRebuilder.
/// </summary>
public sealed class StartupInitializer(
    JobStore repository,
    ElasticIndex elastic,
    IndexingWorker indexing,
    Outbox outbox,
    ILogger<StartupInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= 10 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await repository.InitializeAsync(stoppingToken);
                var result = await elastic.EnsureIndexAsync(stoppingToken);
                if (result == EnsureIndexResult.Ready)
                {
                    logger.LogInformation("Startup concluído: schema PostgreSQL e índice {Index} prontos.", ElasticIndex.IndexName);
                    return;
                }
                if (result == EnsureIndexResult.NeedsRebuild)
                {
                    logger.LogInformation(
                        "Índice {Index} com mapping antigo (salário em double); reconstruindo com scaled_float a partir do PostgreSQL.",
                        ElasticIndex.IndexName);
                    await indexing.RunExclusivelyAsync(
                        ct => IndexRebuilder.RebuildAsync(repository, elastic, outbox, recreateIndex: true, ct),
                        stoppingToken);
                    logger.LogInformation("Migração do índice {Index} concluída.", ElasticIndex.IndexName);
                    return;
                }
                logger.LogWarning("Tentativa {Attempt}: índice de busca indisponível; tentando novamente.", attempt);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tentativa {Attempt} de inicialização falhou; tentando novamente.", attempt);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        logger.LogError("Inicialização de infraestrutura não concluída; endpoints de leitura podem responder 503 até o retry interno.");
    }
}
