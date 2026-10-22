using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Operations;

/// <summary>
/// POST /admin/indexing/reindex — reconstrói o índice a partir de um snapshot transacional
/// (RepeatableRead) do PostgreSQL + watermark do outbox, sob exclusão mútua com o worker e o
/// drain (RunExclusivelyAsync), via IndexRebuilder (padrão compartilhado com a migração
/// automática de índice no startup). Remove do outbox apenas eventos com event_id &lt;=
/// watermark: escritas commitadas depois do snapshot permanecem pendentes e são aplicadas pelo
/// worker/drain — reindex concorrente com escrita não perde atualização.
/// </summary>
public static class ReindexEndpoint
{
    public static void MapReindexEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/indexing/reindex", async (JobStore repo, IndexingWorker indexing, ElasticIndex elastic, Outbox outbox, SearchCacheGeneration generation) =>
        {
            var count = 0;
            await indexing.RunExclusivelyAsync(async ct =>
            {
                count = await IndexRebuilder.RebuildAsync(repo, elastic, outbox, recreateIndex: false, ct);
            });
            generation.Invalidate(); // fora do gate: invalidação de cache não precisa bloquear o worker
            return Results.Ok(new { count });
        });
    }
}
