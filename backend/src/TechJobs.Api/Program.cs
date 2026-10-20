var builder = WebApplication.CreateBuilder(args);

// TODO: registre aqui seus serviços (acesso ao PostgreSQL, cliente Redis,
// cliente Elasticsearch, repositórios, casos de uso, validação, logging
// estruturado...). Connection strings e URLs chegam via docker-compose.yml:
//   ConnectionStrings__Postgres, ConnectionStrings__Redis, Elasticsearch__Url
// TTLs de cache chegam via Cache__DetailsTtlSeconds e Cache__SearchTtlSeconds.

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

// Obrigatório para a suíte de avaliação: 200 quando a API está pronta.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// TODO: implemente o contrato de API descrito no README (seção "CONTRATO DE API"):
//   POST /api/jobs
//   PUT  /api/jobs/{id}
//   GET  /api/jobs/{id}
//   GET  /api/jobs/search
//   GET  /api/jobs/stats
//
// Endpoints dev-only (só ativos com ENABLE_TEST_ENDPOINTS=true — ver README):
//   POST /admin/reset
//   POST /admin/seed
//   GET  /admin/cache-stats
//   POST /admin/faults
//   POST /admin/indexing/drain
//   POST /admin/indexing/reindex

app.Run();
