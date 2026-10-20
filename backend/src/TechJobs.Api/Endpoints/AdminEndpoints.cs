using System.Text.Json;
using TechJobs.Api.Domain;
using TechJobs.Api.Infrastructure;
using TechJobs.Api.Services;

namespace TechJobs.Api.Endpoints;

/// <summary>
/// Endpoints dev-only: mapeados SOMENTE com ENABLE_TEST_ENDPOINTS=true (sem a flag, 404).
/// Nunca ative essa flag fora de ambiente de teste/avaliação.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/reset", async (AdminService admin) =>
        {
            await admin.ResetAsync();
            return Results.Ok(new { status = "ok" });
        });

        app.MapPost("/admin/seed", async (HttpRequest request, AdminService admin) =>
        {
            var (count, seed) = await ReadSeedRequestAsync(request);
            var result = await admin.SeedAsync(count, seed, request.HttpContext.RequestAborted);
            return Results.Ok(new { count = result.Count, seed = result.Seed });
        });

        app.MapGet("/admin/cache-stats", (AdminService admin, CacheOptions options) =>
            Results.Json(admin.GetCacheStats(options)));

        app.MapPost("/admin/faults", async (HttpRequest request, FaultState faults) =>
        {
            using var doc = await ReadJsonAsync(request);
            var root = doc.RootElement;
            if (!root.TryGetProperty("target", out var targetEl)
                || targetEl.GetString() is not { } target
                || target is not ("redis" or "elasticsearch"))
                throw new ValidationException("target é obrigatório e deve ser \"redis\" ou \"elasticsearch\".");
            if (!root.TryGetProperty("enabled", out var enabledEl) || enabledEl.ValueKind != JsonValueKind.True && enabledEl.ValueKind != JsonValueKind.False)
                throw new ValidationException("enabled é obrigatório e deve ser true ou false.");

            var enabled = enabledEl.GetBoolean();
            if (target == "redis") faults.SetRedis(enabled);
            else faults.SetElastic(enabled);
            return Results.Ok(new { target, enabled });
        });

        app.MapPost("/admin/indexing/drain", async (AdminService admin) =>
        {
            await admin.DrainAsync();
            return Results.Ok(new { status = "ok" });
        });

        app.MapPost("/admin/indexing/reindex", async (AdminService admin) =>
        {
            var count = await admin.ReindexAsync();
            return Results.Ok(new { count });
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

    private static async Task<JsonDocument> ReadJsonAsync(HttpRequest request)
    {
        try
        {
            var body = await request.ReadFromJsonAsync<JsonDocument>();
            return body ?? throw new ValidationException("Corpo da requisição inválido.");
        }
        catch (JsonException)
        {
            throw new ValidationException("Corpo da requisição inválido: JSON malformado.");
        }
    }
}
