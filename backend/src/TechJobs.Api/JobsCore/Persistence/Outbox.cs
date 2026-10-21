using Npgsql;

namespace TechJobs.Api.JobsCore.Persistence;

/// <summary>
/// Outbox de indexação: eventos gravados na MESMA transação da vaga (EnqueueAsync) e
/// consumidos pelo IndexingWorker — sincronização eventualmente consistente com o Elasticsearch.
/// </summary>
public sealed class Outbox
{
    private readonly NpgsqlDataSource _dataSource;

    public Outbox(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task EnqueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO outbox_events (job_id) VALUES (@id) ON CONFLICT (job_id) DO UPDATE SET queued_at = now()";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> PeekOutboxAsync(int limit, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT job_id FROM outbox_events ORDER BY queued_at LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", limit);
        var ids = new List<Guid>(Math.Min(limit, 64));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    public async Task RemoveFromOutboxAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await using var cmd = _dataSource.CreateCommand("DELETE FROM outbox_events WHERE job_id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", ids);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> OutboxCountAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT count(*) FROM outbox_events");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ClearOutboxAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM outbox_events");
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
