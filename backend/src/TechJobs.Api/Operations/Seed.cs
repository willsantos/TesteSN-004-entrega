using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Operations;

/// <summary>
/// POST /admin/seed — substitui toda a base por vagas sintéticas determinísticas.
/// Síncrono: ao responder, PostgreSQL e índice já refletem a base e os caches estão limpos.
/// </summary>
public static class SeedEndpoint
{
    public static void MapSeedEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/seed", async (HttpRequest request, JobStore repo, ElasticIndex elastic, CacheService cache, SearchCacheGeneration generation) =>
        {
            var ct = request.HttpContext.RequestAborted;
            var (count, seed) = await ReadSeedRequestAsync(request);
            var jobs = SeedGenerator.Generate(count, seed);

            await repo.TruncateAsync(ct);
            await repo.InsertManyAsync(jobs, ct);

            await elastic.DeleteAllDocumentsAsync(ct);
            await elastic.IndexManyAsync(jobs, ct);
            await elastic.RefreshAsync(ct);

            await cache.FlushAllAsync();
            generation.Invalidate();
            return Results.Ok(new { count, seed });
        });
    }

    private static async Task<(int count, int seed)> ReadSeedRequestAsync(HttpRequest request)
    {
        var count = 5000;
        var seed = 42;
        using var reader = new StreamReader(request.Body);
        var raw = await reader.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("count", out var countEl))
                    {
                        if (countEl.ValueKind != JsonValueKind.Number || !countEl.TryGetInt32(out count) || count is < 1 or > 20000)
                            throw new ValidationException("count deve ser um inteiro entre 1 e 20000.");
                    }
                    if (doc.RootElement.TryGetProperty("seed", out var seedEl))
                    {
                        if (seedEl.ValueKind != JsonValueKind.Number || !seedEl.TryGetInt32(out seed))
                            throw new ValidationException("seed deve ser um inteiro.");
                    }
                }
            }
            catch (JsonException)
            {
                throw new ValidationException("Corpo inválido: envie {\"count\": N, \"seed\": S} (campos opcionais).");
            }
        }
        return (count, seed);
    }
}
