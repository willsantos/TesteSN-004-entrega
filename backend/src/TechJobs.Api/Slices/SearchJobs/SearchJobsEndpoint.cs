using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Slices.SearchJobs;

public static class SearchJobsEndpoint
{
    public static void MapSearchJobsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs/search", async (HttpRequest request, CacheService cache, SearchQueryRunner runner, SearchCacheGeneration generation, CacheOptions cacheOptions) =>
        {
            var parameters = SearchRequestParser.Parse(request.Query, out var errors);
            if (errors.Count > 0)
                throw new ValidationException(string.Join(' ', errors));

            var key = SearchCacheKey.SearchKey(generation.Current, parameters);
            var (outcome, cached) = await cache.GetStringAsync(key, CacheCounters.Kind.Search);
            if (outcome == CacheOutcome.Hit && cached is not null)
                return Results.Content(cached, "application/json");

            var result = await runner.SearchAsync(parameters, request.HttpContext.RequestAborted); // 503 Problem Details se indisponível

            var response = new SearchResponse(
                parameters.Page,
                parameters.PageSize,
                result.Total,
                result.Items.Select(d => new SearchItemResponse(
                    d.Id, d.Title, d.Company, d.Seniority, d.WorkMode, d.City,
                    d.Technologies,
                    ToDecimal(d.SalaryMin), ToDecimal(d.SalaryMax),
                    DateTime.SpecifyKind(d.PublishedAt, DateTimeKind.Utc))).ToList());

            var json = JsonSerializer.Serialize(response, ApiJson.Options);
            if (outcome == CacheOutcome.Miss)
                await cache.SetStringAsync(key, json, TimeSpan.FromSeconds(cacheOptions.SearchTtlSeconds), CacheCounters.Kind.Search);
            return Results.Content(json, "application/json");
        });
    }

    private static decimal? ToDecimal(double? value) =>
        value is null ? null : (decimal)value;
}
