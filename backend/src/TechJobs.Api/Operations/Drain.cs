using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;
using TechJobs.Api.Services;

namespace TechJobs.Api.Operations;

/// <summary>
/// POST /admin/indexing/drain — aplica indexação pendente, faz refresh do índice e
/// invalida o cache de busca: ao retornar, toda escrita confirmada antes da chamada
/// está visível na busca/stats.
/// </summary>
public static class DrainEndpoint
{
    public static void MapDrainEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/indexing/drain", async (IndexingWorker indexing, ElasticIndex elastic, SearchCacheGeneration generation) =>
        {
            await indexing.ProcessPendingAsync();
            await elastic.RefreshAsync();
            generation.Invalidate();
            return Results.Ok(new { status = "ok" });
        });
    }
}
