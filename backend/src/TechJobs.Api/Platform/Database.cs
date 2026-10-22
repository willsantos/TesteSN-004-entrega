using Npgsql;

namespace TechJobs.Api.Platform;

/// <summary>
/// Fonte da verdade transacional. O schema nasce automaticamente no startup
/// (CREATE TABLE IF NOT EXISTS) para que `docker compose up` funcione sem passos manuais.
/// `technologies` fica em jsonb: preserva a grafia/ordem informada, dedup case-insensitive
/// é garantido pela validação, e evita uma tabela normalizada (sem joins para leitura).
/// `outbox_events` registra vagas pendentes de indexação no Elasticsearch, escrita na
/// MESMA transação da vaga — se a API cair após o commit, o evento sobrevive e o worker
/// recupera (sincronização eventualmente consistente). Cada escrita enfileira um evento
/// IMUTÁVEL (N eventos por vaga, PK em event_id bigserial): o consumidor remove exatamente
/// os eventos que indexou, nunca eventos enfileirados depois do peek.
/// O bloco pós-CREATE TABLE é a migração idempotente que atualiza bases antigas
/// (1 linha por job) para o novo formato — roda sempre, sem passos manuais.
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
            event_id  bigserial NOT NULL,
            job_id    uuid NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
            queued_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS outbox_events_queued_at_idx ON outbox_events (queued_at);
        ALTER TABLE outbox_events ADD COLUMN IF NOT EXISTS event_id bigint;
        CREATE SEQUENCE IF NOT EXISTS outbox_events_event_id_seq OWNED BY outbox_events.event_id;
        UPDATE outbox_events SET event_id = nextval('outbox_events_event_id_seq') WHERE event_id IS NULL;
        ALTER TABLE outbox_events ALTER COLUMN event_id SET DEFAULT nextval('outbox_events_event_id_seq');
        ALTER TABLE outbox_events ALTER COLUMN event_id SET NOT NULL;
        ALTER TABLE outbox_events DROP CONSTRAINT IF EXISTS outbox_events_pkey;
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'outbox_events_pk') THEN
                ALTER TABLE outbox_events ADD CONSTRAINT outbox_events_pk PRIMARY KEY (event_id);
            END IF;
        END $$;
        CREATE INDEX IF NOT EXISTS outbox_events_job_id_idx ON outbox_events (job_id);
        """;

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        return builder.Build();
    }
}
