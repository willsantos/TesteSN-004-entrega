using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Operations;

/// <summary>POST /admin/faults — liga/desliga falhas simuladas em Redis ou Elasticsearch.</summary>
public static class FaultsEndpoint
{
    public static void MapFaultsEndpoint(this IEndpointRouteBuilder app)
    {
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
