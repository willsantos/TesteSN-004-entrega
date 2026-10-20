using Npgsql;

namespace TechJobs.Api.Infrastructure;

/// <summary>
/// Fonte da verdade transacional. O schema nasce automaticamente no startup
/// (CREATE TABLE IF NOT EXISTS) para que `docker compose up` funcione sem passos manuais.
/// `technologies` fica em jsonb: preserva a grafia/ordem informada, dedup case-insensitive
/// é garantido pela validação, e evita uma tabela normalizada (sem joins para leitura).
/// `outbox_events` registra vagas pendentes de indexação no Elasticsearch, escrita na
/// MESMA transação da vaga — se a API cair após o commit, o evento sobrevive e o worker
/// recupera (sincronização eventualmente consistente).
/// </summary>
public static class Database
{
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS jobs (
            id           uuid PRIMARY KEY,
            title        text NOT NULL,
            company      text NOT NULL,
            description  text NOT NULL,
            technologies jsonb NOT NULL,
            seniority    text NOT NULL,
            work_mode    text NOT NULL,
            city         text,
            salary_min   numeric(14,2),
            salary_max   numeric(14,2),
            status       text NOT NULL,
            published_at timestamptz NOT NULL,
            updated_at   timestamptz NOT NULL
        );
        CREATE TABLE IF NOT EXISTS outbox_events (
            job_id    uuid PRIMARY KEY REFERENCES jobs(id) ON DELETE CASCADE,
            queued_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS outbox_events_queued_at_idx ON outbox_events (queued_at);
        """;

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        return builder.Build();
    }
}
