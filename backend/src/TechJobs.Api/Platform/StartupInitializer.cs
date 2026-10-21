using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Elastic;

namespace TechJobs.Api.Platform;

/// <summary>
/// Garante schema do PostgreSQL e índice do Elasticsearch no startup, com retry
/// (dependências sobem saudáveis pelo docker-compose, mas desempates não custam).
/// </summary>
public sealed class StartupInitializer(
    JobStore repository,
    ElasticIndex elastic,
    ILogger<StartupInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= 10 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await repository.InitializeAsync(stoppingToken);
                if (await elastic.EnsureIndexAsync(stoppingToken))
                {
                    logger.LogInformation("Startup concluído: schema PostgreSQL e índice {Index} prontos.", ElasticIndex.IndexName);
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
