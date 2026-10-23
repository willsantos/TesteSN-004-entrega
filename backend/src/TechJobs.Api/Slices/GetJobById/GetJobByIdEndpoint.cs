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
        app.MapGet("/api/jobs/{id:guid}", async (Guid id, HttpContext context, CacheService cache, JobStore store, CacheOptions cacheOptions, SingleFlight singleFlight) =>
        {
            var key = CacheKeys.DetailsKey(id);

            // Preenchimento coalescido por chave de detalhe: em rajada fria, um único
            // request (líder) lê o PostgreSQL e popula; os seguidores revalidam o cache
            // (hit do valor que o líder acabou de gravar) ou reutilizam o payload dele.
            var (json, executedFactory) = await singleFlight.CoalesceAsync(key, async _ =>
            {
                var (outcome, cached) = await cache.GetStringAsync(key, CacheCounters.Kind.Details);
                if (outcome == CacheOutcome.Hit && cached is not null)
                    return cached;
                if (outcome == CacheOutcome.Error)
                    return null; // Redis indisponível: sinaliza fonte oficial, sem popular
                var job = await store.GetByIdAsync(id) ?? throw new JobNotFoundException(id);
                var payload = JsonSerializer.Serialize(JobResponse.From(job), ApiJson.Options);
                await cache.SetStringAsync(key, payload, TimeSpan.FromSeconds(cacheOptions.DetailsTtlSeconds), CacheCounters.Kind.Details);
                return payload;
            }, context.RequestAborted);

            if (executedFactory)
            {
                if (json is not null)
                    return Results.Content(json, "application/json");
                // Error do Redis: responde pelo PostgreSQL, sem tentar popular (sem cache negativo).
                var jobDirect = await store.GetByIdAsync(id) ?? throw new JobNotFoundException(id);
                return Results.Content(JsonSerializer.Serialize(JobResponse.From(jobDirect), ApiJson.Options), "application/json");
            }

            // Seguidor: leitura própria conta o hit; se a chave não sobreviveu (expurgo
            // ou queda do Redis no meio da rajada), usa o payload do líder ou a fonte.
            var (followerOutcome, followerCached) = await cache.GetStringAsync(key, CacheCounters.Kind.Details);
            if (followerOutcome == CacheOutcome.Hit && followerCached is not null)
                return Results.Content(followerCached, "application/json");
            if (json is not null)
                return Results.Content(json, "application/json");
            var jobFallback = await store.GetByIdAsync(id) ?? throw new JobNotFoundException(id);
            return Results.Content(JsonSerializer.Serialize(JobResponse.From(jobFallback), ApiJson.Options), "application/json");
        });
    }
}
