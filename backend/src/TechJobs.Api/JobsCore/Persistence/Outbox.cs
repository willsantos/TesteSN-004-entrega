using Npgsql;

namespace TechJobs.Api.JobsCore.Persistence;

/// <summary>Evento imutável do outbox de indexação (uma linha por escrita na vaga).</summary>
public sealed record OutboxEvent(long EventId, Guid JobId);

/// <summary>
/// Outbox de indexação: eventos IMUTÁVEIS gravados na MESMA transação da vaga (EnqueueAsync) e
/// consumidos pelo IndexingWorker — sincronização eventualmente consistente com o Elasticsearch.
/// Um job pode acumular N eventos (um por escrita); o consumidor remove EXATAMENTE os eventos
/// que indexou (RemoveFromOutboxAsync por event_id), nunca eventos enfileirados após o peek —
/// sem perda de atualização. Consumo em ordem de enfileiramento (ORDER BY event_id).
/// </summary>
public sealed class Outbox
{
    private readonly NpgsqlDataSource _dataSource;

    public Outbox(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task EnqueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO outbox_events (job_id) VALUES (@id)";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<OutboxEvent>> PeekOutboxAsync(int limit, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT event_id, job_id FROM outbox_events ORDER BY event_id LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", limit);
        var events = new List<OutboxEvent>(Math.Min(limit, 64));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) events.Add(new OutboxEvent(reader.GetInt64(0), reader.GetGuid(1)));
        return events;
    }

    /// <summary>Remove EXATAMENTE os eventos consumidos; eventos enfileirados depois do peek sobrevivem.</summary>
    public async Task RemoveFromOutboxAsync(IReadOnlyList<long> eventIds, CancellationToken ct = default)
    {
        if (eventIds.Count == 0) return;
        await using var cmd = _dataSource.CreateCommand("DELETE FROM outbox_events WHERE event_id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", eventIds);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> OutboxCountAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT count(*) FROM outbox_events");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Remove eventos até o watermark (inclusive) — usado pelo reindex: o watermark é lido na
    /// mesma transação do snapshot, então qualquer evento commitado depois (event_id maior)
    /// sobrevive e é aplicado pelo worker/drain.
    /// </summary>
    public async Task DeleteUpToAsync(long watermark, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM outbox_events WHERE event_id <= @watermark");
        cmd.Parameters.AddWithValue("watermark", watermark);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
