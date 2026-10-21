using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Platform.Elastic;

/// <summary>
/// Projeção de busca no índice `techjobs-v1`. Toda operação checa a falha simulada
/// primeiro e trata resposta inválida como indisponibilidade — a leitura pública
/// devolve 503; a indexação (worker) apenas deixa o evento pendente no outbox.
/// </summary>
public sealed class ElasticIndex
{
    public const string IndexName = "techjobs-v1";
    private const int BulkChunkSize = 2000;

    private readonly ElasticsearchClient _client;
    private readonly FaultState _faults;
    private readonly ILogger<ElasticIndex> _logger;

    public ElasticIndex(ElasticsearchClient client, FaultState faults, ILogger<ElasticIndex> logger)
    {
        _client = client;
        _faults = faults;
        _logger = logger;
    }

    private void EnsureAvailable()
    {
        if (_faults.ElasticDown)
            throw new SearchUnavailableException("A busca está temporariamente indisponível (Elasticsearch indisponível).");
    }

    /// <summary>Cria o índice com mapping explícito se ainda não existir. Idempotente.</summary>
    public async Task<bool> EnsureIndexAsync(CancellationToken ct = default)
    {
        var exists = await _client.Indices.ExistsAsync(IndexName, ct);
        if (exists.Exists) return true;

        var response = await _client.Indices.CreateAsync<JobDoc>(IndexName, c => c
            .Settings(s => s
                .NumberOfShards(1)
                .NumberOfReplicas(0))
            .Mappings(m => m
                .Properties(p => p
                    .Keyword("id")
                    .Text("title", t => t.Analyzer("portuguese"))
                    .Text("description", t => t.Analyzer("portuguese"))
                    .Text("company", t => t.Fields(f => f.Keyword("raw")))
                    .Keyword("technologies")
                    .Keyword("techKeys")
                    .Keyword("seniority")
                    .Keyword("workMode")
                    .Keyword("city")
                    .Keyword("status")
                    .DoubleNumber("salaryMin")
                    .DoubleNumber("salaryMax")
                    .Date("publishedAt")
                    .Date("updatedAt"))), ct);

        if (!response.IsValidResponse && !IsIndexAlreadyExists(response.DebugInformation))
        {
            _logger.LogWarning("Criação do índice {Index} falhou: {Error}", IndexName, response.DebugInformation);
            return false;
        }
        return true;
    }

    private static bool IsIndexAlreadyExists(string? debug) =>
        debug?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true
        || debug?.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase) == true;

    public async Task DropAndCreateIndexAsync(CancellationToken ct = default)
    {
        EnsureAvailable();
        await _client.Indices.DeleteAsync(IndexName, ct);
        var created = await EnsureIndexAsync(ct);
        if (!created) throw new SearchUnavailableException("Falha ao recriar o índice de busca.");
    }

    public async Task<long> DeleteAllDocumentsAsync(CancellationToken ct = default)
    {
        EnsureAvailable();
        var response = await _client.DeleteByQueryAsync(IndexName, d => d
            .Query(q => q.MatchAll(m => { }))
            .Conflicts(Conflicts.Proceed), ct);
        if (!response.IsValidResponse)
            throw new SearchUnavailableException("Falha ao limpar o índice de busca.");
        return response.Deleted ?? 0;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        EnsureAvailable();
        var response = await _client.Indices.RefreshAsync(IndexName, ct);
        if (!response.IsValidResponse)
            throw new SearchUnavailableException("Falha ao aplicar refresh do índice.");
    }

    public async Task IndexJobAsync(Job job, CancellationToken ct = default)
    {
        EnsureAvailable();
        var response = await _client.IndexAsync(JobDoc.From(job), i => i.Index(IndexName).Id(job.Id), ct);
        if (!response.IsValidResponse)
            throw new SearchUnavailableException("Falha ao indexar vaga no Elasticsearch.");
    }

    /// <summary>Bulk index com id explícito (id do documento = id da vaga → upsert, nunca duplica).</summary>
    public async Task IndexManyAsync(IReadOnlyList<Job> jobs, CancellationToken ct = default)
    {
        if (jobs.Count == 0) return;
        EnsureAvailable();
        foreach (var chunk in jobs.Chunk(BulkChunkSize))
        {
            var operations = new List<IBulkOperation>(chunk.Length);
            foreach (var job in chunk)
            {
                operations.Add(new BulkIndexOperation<JobDoc>(JobDoc.From(job))
                {
                    Id = job.Id,
                    Index = IndexName,
                });
            }
            var request = new BulkRequest
            {
                Operations = new BulkOperationsCollection([.. operations]),
            };
            var response = await _client.BulkAsync(request, ct);
            if (!response.IsValidResponse || response.Errors)
                throw new SearchUnavailableException("Falha ao indexar lote de vagas no Elasticsearch.");
        }
    }
}
