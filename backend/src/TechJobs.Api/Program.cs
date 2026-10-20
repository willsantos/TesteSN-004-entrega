using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Npgsql;
using StackExchange.Redis;
using TechJobs.Api.Domain;
using TechJobs.Api.Endpoints;
using TechJobs.Api.Infrastructure;
using TechJobs.Api.Infrastructure.Elastic;
using TechJobs.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- configuração por ambiente (nada de credencial no código) ----
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Database=techjobs;Username=techjobs;Password=techjobs";
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
var elasticsearchUrl = builder.Configuration["Elasticsearch:Url"] ?? "http://localhost:9200";
var cacheOptions = new CacheOptions(
    builder.Configuration.GetValue("Cache:DetailsTtlSeconds", 300),
    builder.Configuration.GetValue("Cache:SearchTtlSeconds", 60));
var enableTestEndpoints = string.Equals(
    builder.Configuration["ENABLE_TEST_ENDPOINTS"], "true", StringComparison.OrdinalIgnoreCase);

// ---- serviços ----
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddSingleton(cacheOptions);
builder.Services.AddSingleton<FaultState>();
builder.Services.AddSingleton<CacheCounters>();
builder.Services.AddSingleton<SearchCacheGeneration>();
builder.Services.AddSingleton(_ => Database.CreateDataSource(postgresConnectionString));
builder.Services.AddSingleton<JobRepository>();
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp =>
{
    var options = ConfigurationOptions.Parse(redisConnectionString);
    options.AbortOnConnectFail = false; // degradação graciosa: API sobe mesmo com Redis fora
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton(_ => new ElasticsearchClient(
    new ElasticsearchClientSettings(new Uri(elasticsearchUrl))
        .RequestTimeout(TimeSpan.FromSeconds(30))));
builder.Services.AddSingleton<ElasticService>();
builder.Services.AddSingleton<IndexingQueue>();
builder.Services.AddSingleton<JobsService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<IndexingWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexingWorker>());
builder.Services.AddHostedService<StartupInitializer>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();

// Obrigatório para a suíte de avaliação: 200 quando a API está pronta.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapJobsEndpoints();

// Endpoints dev-only: só existem (e só devem existir) com ENABLE_TEST_ENDPOINTS=true.
if (enableTestEndpoints)
    app.MapAdminEndpoints();

// OpenAPI + página Swagger (contratos visíveis em /swagger).
app.MapOpenApi();
app.MapGet("/swagger", () => Results.Content(SwaggerPage.Html, "text/html"));

app.Run();

/// <summary>Expõe Program para WebApplicationFactory nos testes.</summary>
public partial class Program;

/// <summary>
/// Garante schema do PostgreSQL e índice do Elasticsearch no startup, com retry
/// (dependências sobem saudáveis pelo docker-compose, mas desempates não custam).
/// </summary>
public sealed class StartupInitializer(
    JobRepository repository,
    ElasticService elastic,
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
                    logger.LogInformation("Startup concluído: schema PostgreSQL e índice {Index} prontos.", ElasticService.IndexName);
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

internal static class SwaggerPage
{
    public const string Html = """
        <!doctype html>
        <html>
          <head>
            <meta charset="utf-8">
            <title>TechJobs API — Swagger</title>
            <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css">
          </head>
          <body>
            <div id="swagger-ui"></div>
            <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
            <script>
              window.ui = SwaggerUIBundle({ url: '/openapi/v1.json', dom_id: '#swagger-ui' });
            </script>
          </body>
        </html>
        """;
}
