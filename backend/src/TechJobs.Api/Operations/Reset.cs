using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Operations;

/// <summary>
/// POST /admin/reset — limpa PostgreSQL, recria o índice vazio, limpa Redis,
/// zera contadores e desliga falhas simuladas.
/// </summary>
public static class ResetEndpoint
{
    public static void MapResetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/reset", async (FaultState faults, JobStore repo, ElasticIndex elastic, CacheService cache, CacheCounters counters, SearchCacheGeneration generation) =>
        {
            faults.Reset();
            await repo.TruncateAsync();
            await elastic.DropAndCreateIndexAsync();
            await cache.FlushAllAsync();
            counters.Reset();
            generation.Invalidate();
            return Results.Ok(new { status = "ok" });
        });
    }
}
