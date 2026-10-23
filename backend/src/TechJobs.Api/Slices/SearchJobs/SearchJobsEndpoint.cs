using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Slices.SearchJobs;

public static class SearchJobsEndpoint
{
    public static void MapSearchJobsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs/search", async (HttpRequest request, CacheService cache, SearchQueryRunner runner, SearchCacheGeneration generation, CacheOptions cacheOptions, SingleFlight singleFlight) =>
        {
            var parameters = SearchRequestParser.Parse(request.Query, out var errors);
            if (errors.Count > 0)
                throw new ValidationException(string.Join(' ', errors));

            var aborted = request.HttpContext.RequestAborted;
            var key = SearchCacheKey.SearchKey(generation.Current, parameters);

            // Preenchimento coalescido por chave canônica de busca: em rajada fria, um
            // único request (líder) consulta o Elasticsearch e popula; os seguidores
            // revalidam o cache ou reutilizam o payload do líder.
            var (json, executedFactory) = await singleFlight.CoalesceAsync(key, async ct =>
            {
                var (outcome, cached) = await cache.GetStringAsync(key, CacheCounters.Kind.Search);
                if (outcome == CacheOutcome.Hit && cached is not null)
                    return cached;
                if (outcome == CacheOutcome.Error)
                    return null; // Redis indisponível: sinaliza consulta direta, sem popular
                var payload = await SearchFromSourceAsync(runner, parameters, ct);
                await cache.SetStringAsync(key, payload, TimeSpan.FromSeconds(cacheOptions.SearchTtlSeconds), CacheCounters.Kind.Search);
                return payload;
            }, aborted);

            if (executedFactory)
            {
                if (json is not null)
                    return Results.Content(json, "application/json");
                // Error do Redis: consulta o Elasticsearch sem tentar popular (como antes).
                return Results.Content(await SearchFromSourceAsync(runner, parameters, aborted), "application/json");
            }

            // Seguidor: leitura própria conta o hit; se a chave não sobreviveu, usa o
            // payload do líder ou vai direto ao Elasticsearch (líder viu Redis fora).
            var (followerOutcome, followerCached) = await cache.GetStringAsync(key, CacheCounters.Kind.Search);
            if (followerOutcome == CacheOutcome.Hit && followerCached is not null)
                return Results.Content(followerCached, "application/json");
            if (json is not null)
                return Results.Content(json, "application/json");
            return Results.Content(await SearchFromSourceAsync(runner, parameters, aborted), "application/json");
        });
    }

    private static async Task<string> SearchFromSourceAsync(
        SearchQueryRunner runner, SearchQueryParams parameters, CancellationToken ct)
    {
        var result = await runner.SearchAsync(parameters, ct); // 503 Problem Details se indisponível

        var response = new SearchResponse(
            parameters.Page,
            parameters.PageSize,
            result.Total,
            result.Items.Select(d => new SearchItemResponse(
                d.Id, d.Title, d.Company, d.Seniority, d.WorkMode, d.City,
                d.Technologies,
                ToDecimal(d.SalaryMin), ToDecimal(d.SalaryMax),
                DateTime.SpecifyKind(d.PublishedAt, DateTimeKind.Utc))).ToList());

        return JsonSerializer.Serialize(response, ApiJson.Options);
    }

    /// <summary>
    /// O índice guarda salário em scaled_float (double com escala 2, fator 100): o round
    /// para 2 casas (away from zero, igual ao numeric(14,2) do PostgreSQL) garante os
    /// centavos exatos na saída decimal — mesma resposta da escrita e do GET por id.
    /// </summary>
    private static decimal? ToDecimal(double? value) =>
        value is null ? null : Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
}
