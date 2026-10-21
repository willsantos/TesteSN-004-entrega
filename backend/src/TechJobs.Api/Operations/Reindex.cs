using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Operations;

/// <summary>
/// POST /admin/indexing/reindex — reconstrói o índice a partir do PostgreSQL
/// (ids estáveis → nunca duplica) e limpa o outbox.
/// </summary>
public static class ReindexEndpoint
{
    public static void MapReindexEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/indexing/reindex", async (JobStore repo, ElasticIndex elastic, Outbox outbox, SearchCacheGeneration generation) =>
        {
            var jobs = await repo.GetAllAsync();
            await elastic.DeleteAllDocumentsAsync();
            await elastic.IndexManyAsync(jobs);
            await elastic.RefreshAsync();
            await outbox.ClearOutboxAsync();
            generation.Invalidate();
            return Results.Ok(new { count = jobs.Count });
        });
    }
}
