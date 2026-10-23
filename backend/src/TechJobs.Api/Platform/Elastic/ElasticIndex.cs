using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Platform.Elastic;

/// <summary>Resultado da garantia de índice no startup.</summary>
public enum EnsureIndexResult
{
    /// <summary>Índice existe (ou foi criado agora) com o mapping atual — pronto para uso.</summary>
    Ready,
    /// <summary>
    /// Índice existe, mas com mapping antigo (salário em double): mudança de tipo de campo
    /// não é aplicável in-place no Elasticsearch — o chamador deve reconstruir a projeção.
    /// </summary>
    NeedsRebuild,
    /// <summary>Falha ao consultar/criar o índice (ex.: Elasticsearch indisponível): tentar de novo.</summary>
    Unavailable,
}

/// <summary>
/// Projeção de busca no índice `techjobs-v1`. Toda operação checa a falha simulada
/// primeiro e trata resposta inválida como indisponibilidade — a leitura pública
/// devolve 503; a indexação (worker) apenas deixa o evento pendente no outbox.
/// </summary>
public sealed class ElasticIndex
{
    public const string IndexName = "techjobs-v1";
    /// <summary>Janela from+size configurada no índice: paginação profunda sem 503.</summary>
    public const int MaxResultWindow = 50_000;
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

    /// <summary>
    /// Cria o índice com mapping explícito se ainda não existir; se existir, aplica settings
    /// dinâmicos e verifica o tipo do campo salarial. Idempotente. Mudança de tipo de campo
    /// (double → scaled_float) não é aplicável in-place no Elasticsearch: um índice criado
    /// por base antiga sinaliza NeedsRebuild ao chamador (migração automática no startup) —
    /// sem rebuild às cegas se o ES estiver fora (Unavailable → retry do StartupInitializer).
    /// </summary>
    public async Task<EnsureIndexResult> EnsureIndexAsync(CancellationToken ct = default)
    {
        var exists = await _client.Indices.ExistsAsync(IndexName, ct);
        if (exists.Exists)
        {
            // max_result_window é setting dinâmico: índices criados antes desta versão ganham a
            // janela estendida na primeira subida bem-sucedida. Falha não derruba o startup.
            var settings = await _client.Indices.PutSettingsAsync(
                new IndexSettings { MaxResultWindow = MaxResultWindow }, IndexName, ct);
            if (!settings.IsValidResponse)
                _logger.LogWarning("Aplicação de max_result_window no índice {Index} falhou: {Error}",
                    IndexName, settings.DebugInformation);
            return await SalaryMappingNeedsRebuildAsync(ct);
        }

        var response = await _client.Indices.CreateAsync<JobDoc>(IndexName, c => c
            .Settings(s => s
                .NumberOfShards(1)
                .NumberOfReplicas(0)
                .MaxResultWindow(MaxResultWindow))
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
                    // scaled_float com escala 2 (fator 100): centavos exatos, tipo numérico
                    // com escala definida (seção 10 do spec) — coerente com numeric(14,2).
                    .ScaledFloatNumber("salaryMin", t => t.ScalingFactor(100))
                    .ScaledFloatNumber("salaryMax", t => t.ScalingFactor(100))
                    .Date("publishedAt")
                    .Date("updatedAt"))), ct);

        if (!response.IsValidResponse && !IsIndexAlreadyExists(response.DebugInformation))
        {
            _logger.LogWarning("Criação do índice {Index} falhou: {Error}", IndexName, response.DebugInformation);
            return EnsureIndexResult.Unavailable;
        }
        return EnsureIndexResult.Ready;
    }

    /// <summary>Verifica se o índice existente ainda tem salário como double (mapping antigo).</summary>
    private async Task<EnsureIndexResult> SalaryMappingNeedsRebuildAsync(CancellationToken ct)
    {
        var mapping = await _client.Indices.GetMappingAsync(new GetMappingRequest(IndexName), ct);
        if (!mapping.IsValidResponse)
        {
            // Sem leitura de mapping não há reconstrução às cegas: retry no próximo loop.
            _logger.LogWarning("Leitura do mapping do índice {Index} falhou: {Error}",
                IndexName, mapping.DebugInformation);
            return EnsureIndexResult.Unavailable;
        }
        if (mapping.Indices.TryGetValue(IndexName, out var record)
            && record.Mappings.Properties.TryGetProperty("salaryMin", out var salaryMin)
            && string.Equals(salaryMin.Type, "scaled_float", StringComparison.Ordinal))
            return EnsureIndexResult.Ready;
        return EnsureIndexResult.NeedsRebuild;
    }

    private static bool IsIndexAlreadyExists(string? debug) =>
        debug?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true
        || debug?.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase) == true;

    public async Task DropAndCreateIndexAsync(CancellationToken ct = default)
    {
        EnsureAvailable();
        await _client.Indices.DeleteAsync(IndexName, ct);
        var created = await EnsureIndexAsync(ct);
        if (created != EnsureIndexResult.Ready)
            throw new SearchUnavailableException("Falha ao recriar o índice de busca.");
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
