using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Slices.GetJobById;

public static class GetJobByIdEndpoint
{
    public static void MapGetJobByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs/{id:guid}", async (Guid id, CacheService cache, JobStore store, CacheOptions cacheOptions) =>
        {
            var key = CacheKeys.DetailsKey(id);
            var (outcome, cached) = await cache.GetStringAsync(key, CacheCounters.Kind.Details);
            if (outcome == CacheOutcome.Hit && cached is not null)
                return Results.Content(cached, "application/json");

            var job = await store.GetByIdAsync(id) ?? throw new JobNotFoundException(id);

            var json = JsonSerializer.Serialize(JobResponse.From(job), ApiJson.Options);
            if (outcome == CacheOutcome.Miss)
                await cache.SetStringAsync(key, json, TimeSpan.FromSeconds(cacheOptions.DetailsTtlSeconds), CacheCounters.Kind.Details);
            // outcome == Error: Redis indisponível — responde pelo PostgreSQL, sem tentar popular.
            return Results.Content(json, "application/json");
        });
    }
}
