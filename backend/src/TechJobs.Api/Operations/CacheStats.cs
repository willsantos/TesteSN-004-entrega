using TechJobs.Api.Contracts;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Operations;

/// <summary>GET /admin/cache-stats — configuração de TTL e contadores de hit/miss/error por cache.</summary>
public static class CacheStatsEndpoint
{
    public static void MapCacheStatsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/cache-stats", (CacheCounters counters, CacheOptions options) =>
            Results.Json(new CacheStatsResponse(
                new CacheStatsConfig(options.DetailsTtlSeconds, options.SearchTtlSeconds),
                new CounterSnapshot(counters.Details.Hit, counters.Details.Miss, counters.Details.Error),
                new CounterSnapshot(counters.Search.Hit, counters.Search.Miss, counters.Search.Error))));
    }
}
