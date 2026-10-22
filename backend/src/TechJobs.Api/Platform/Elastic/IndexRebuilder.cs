using TechJobs.Api.JobsCore;
using TechJobs.Api.JobsCore.Persistence;

namespace TechJobs.Api.Platform.Elastic;

/// <summary>
/// Reconstrução completa da projeção de busca a partir do PostgreSQL (fonte da verdade),
/// padrão compartilhado pelo reindex administrativo (Operations/Reindex.cs) e pela
/// migração automática de índice no startup (mudança de tipo de campo — double →
/// scaled_float — não é aplicável in-place no Elasticsearch). Snapshot transacional
/// (RepeatableRead) + watermark do outbox lidos na MESMA transação: a limpeza do outbox
/// remove apenas eventos com event_id &lt;= watermark — escritas commitadas depois do
/// snapshot permanecem pendentes e são aplicadas pelo worker/drain (sem perda de
/// atualização em reindex concorrente com escrita).
/// </summary>
public static class IndexRebuilder
{
    /// <summary>
    /// Executa a reconstrução: snapshot + watermark → (opcionalmente derruba e recria o
    /// índice com o mapping atual) → bulk index → refresh → limpeza do outbox até o
    /// watermark. Deve rodar sob exclusão mútua com o worker/drain
    /// (IndexingWorker.RunExclusivelyAsync). Retorna o tamanho do snapshot.
    /// </summary>
    public static async Task<int> RebuildAsync(
        JobStore repo, ElasticIndex elastic, Outbox outbox, bool recreateIndex, CancellationToken ct)
    {
        var (snapshot, watermark) = await repo.SnapshotAllWithWatermarkAsync(ct);
        if (recreateIndex)
            await elastic.DropAndCreateIndexAsync(ct); // mapping novo: drop + create (não aplicável in-place)
        else
            await elastic.DeleteAllDocumentsAsync(ct);
        await elastic.IndexManyAsync(snapshot, ct);
        await elastic.RefreshAsync(ct);
        await outbox.DeleteUpToAsync(watermark, ct);
        return snapshot.Count;
    }
}
