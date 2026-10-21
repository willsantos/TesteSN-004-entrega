using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TechJobs.Api.JobsCore;
using TechJobs.Api.Platform;

namespace TechJobs.Api.JobsCore.Persistence;

/// <summary>Acesso a dados puro Npgsql (sem ORM): SQL explícito e controle total das transações.</summary>
public sealed class JobStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Outbox _outbox;
    private static readonly JsonSerializerOptions TechJson = new(JsonSerializerDefaults.Web);

    public JobStore(NpgsqlDataSource dataSource, Outbox outbox)
    {
        _dataSource = dataSource;
        _outbox = outbox;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(Database.SchemaSql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT id, title, company, description, technologies, seniority, work_mode, city, salary_min, salary_max, status, published_at, updated_at FROM jobs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapJob(reader) : null;
    }

    public async Task<IReadOnlyList<Job>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        await using var cmd = _dataSource.CreateCommand(
            "SELECT id, title, company, description, technologies, seniority, work_mode, city, salary_min, salary_max, status, published_at, updated_at FROM jobs WHERE id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", ids);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT id, title, company, description, technologies, seniority, work_mode, city, salary_min, salary_max, status, published_at, updated_at FROM jobs ORDER BY id");
        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>INSERT da vaga + evento de outbox na mesma transação (indexação eventual).</summary>
    public async Task<Job> CreateAsync(Guid id, ValidatedJob job, DateTime now, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO jobs (id, title, company, description, technologies, seniority, work_mode, city, salary_min, salary_max, status, published_at, updated_at)
                VALUES (@id, @title, @company, @description, @technologies, @seniority, @work_mode, @city, @salary_min, @salary_max, @status, @published_at, @updated_at)
                """;
            BindJob(cmd, id, job, now, now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await _outbox.EnqueueAsync(conn, tx, id, ct);
        await tx.CommitAsync(ct);
        return ToJob(id, job, now, now);
    }

    /// <summary>
    /// Substituição completa (PUT). Idempotente: se o conteúdo de domínio não muda,
    /// updatedAt é preservado — o mesmo payload reaplicado deixa a vaga no mesmo estado.
    /// </summary>
    public async Task<Job?> ReplaceAsync(Guid id, ValidatedJob job, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Job current;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, title, company, description, technologies, seniority, work_mode, city, salary_min, salary_max, status, published_at, updated_at FROM jobs WHERE id = @id FOR UPDATE";
            cmd.Parameters.AddWithValue("id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null; // tx é desfeita pela disposição (reader/cmd fechados antes)
            current = MapJob(reader);
        }

        if (current.SameContentAs(job))
        {
            await tx.CommitAsync(ct);
            return current; // nada mudou: estado e updatedAt preservados
        }

        var now = DateTime.UtcNow;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE jobs SET title = @title, company = @company, description = @description, technologies = @technologies,
                                seniority = @seniority, work_mode = @work_mode, city = @city, salary_min = @salary_min,
                                salary_max = @salary_max, status = @status, updated_at = @updated_at
                WHERE id = @id
                """;
            BindJob(cmd, id, job, current.PublishedAt, now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await _outbox.EnqueueAsync(conn, tx, id, ct);
        await tx.CommitAsync(ct);
        return ToJob(id, job, current.PublishedAt, now);
    }

    public async Task TruncateAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("TRUNCATE TABLE jobs, outbox_events");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Bulk insert via COPY binário — usado pelo seed (até 20k vagas em ~1s).</summary>
    public async Task InsertManyAsync(IReadOnlyList<Job> jobs, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY jobs (id, title, company, description, technologies, seniority, work_mode, city, salary_min, salary_max, status, published_at, updated_at) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var job in jobs)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(job.Id, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(job.Title, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(job.Company, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(job.Description, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(JsonSerializer.Serialize(job.Technologies, TechJson), NpgsqlDbType.Jsonb, ct);
                await writer.WriteAsync(job.Seniority.ToApi(), NpgsqlDbType.Text, ct);
                await writer.WriteAsync(job.WorkMode.ToApi(), NpgsqlDbType.Text, ct);
                if (job.City is null) await writer.WriteNullAsync(ct); else await writer.WriteAsync(job.City, NpgsqlDbType.Text, ct);
                if (job.SalaryMin is null) await writer.WriteNullAsync(ct); else await writer.WriteAsync(job.SalaryMin, NpgsqlDbType.Numeric, ct);
                if (job.SalaryMax is null) await writer.WriteNullAsync(ct); else await writer.WriteAsync(job.SalaryMax, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(job.Status.ToApi(), NpgsqlDbType.Text, ct);
                await writer.WriteAsync(job.PublishedAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(job.UpdatedAt, NpgsqlDbType.TimestampTz, ct);
            }
            await writer.CompleteAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    // ---- helpers ----

    private static async Task<IReadOnlyList<Job>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var jobs = new List<Job>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) jobs.Add(MapJob(reader));
        return jobs;
    }

    private static Job MapJob(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        JsonSerializer.Deserialize<List<string>>(r.GetString(4), TechJson) ?? [],
        JobEnums.TryParseSeniority(r.GetString(5))!.Value,
        JobEnums.TryParseWorkMode(r.GetString(6))!.Value,
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetDecimal(8),
        r.IsDBNull(9) ? null : r.GetDecimal(9),
        JobEnums.TryParseStatus(r.GetString(10))!.Value,
        DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc),
        DateTime.SpecifyKind(r.GetDateTime(12), DateTimeKind.Utc));

    private static Job ToJob(Guid id, ValidatedJob v, DateTime publishedAt, DateTime updatedAt) => new(
        id, v.Title, v.Company, v.Description, v.Technologies, v.Seniority, v.WorkMode,
        v.City, v.SalaryMin, v.SalaryMax, v.Status, publishedAt, updatedAt);

    private static void BindJob(NpgsqlCommand cmd, Guid id, ValidatedJob v, DateTime publishedAt, DateTime updatedAt)
    {
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("title", v.Title);
        cmd.Parameters.AddWithValue("company", v.Company);
        cmd.Parameters.AddWithValue("description", v.Description);
        cmd.Parameters.AddWithValue("technologies", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(v.Technologies, TechJson));
        cmd.Parameters.AddWithValue("seniority", v.Seniority.ToApi());
        cmd.Parameters.AddWithValue("work_mode", v.WorkMode.ToApi());
        if (v.City is null) cmd.Parameters.AddWithValue("city", DBNull.Value); else cmd.Parameters.AddWithValue("city", v.City);
        if (v.SalaryMin is null) cmd.Parameters.AddWithValue("salary_min", DBNull.Value); else cmd.Parameters.AddWithValue("salary_min", v.SalaryMin.Value);
        if (v.SalaryMax is null) cmd.Parameters.AddWithValue("salary_max", DBNull.Value); else cmd.Parameters.AddWithValue("salary_max", v.SalaryMax.Value);
        cmd.Parameters.AddWithValue("status", v.Status.ToApi());
        cmd.Parameters.AddWithValue("published_at", publishedAt);
        cmd.Parameters.AddWithValue("updated_at", updatedAt);
    }
}
