using System.Text.Json;
using Microsoft.Extensions.Logging;
using TechJobs.Api.Domain;
using TechJobs.Api.Infrastructure;
using TechJobs.Api.Infrastructure.Elastic;

namespace TechJobs.Api.Services;

/// <summary>
/// Orquestra PostgreSQL (verdade), Redis (cache) e Elasticsearch (busca) nos fluxos da API.
///leituras públicas devolvem JSON pronto (o cache armazena exatamente o que será respondido).
/// </summary>
public sealed class JobsService
{
    private readonly JobRepository _repo;
    private readonly CacheService _cache;
    private readonly ElasticService _elastic;
    private readonly SearchCacheGeneration _generation;
    private readonly CacheOptions _cacheOptions;
    private readonly IndexingQueue _indexingQueue;
    private readonly ILogger<JobsService> _logger;

    public JobsService(
        JobRepository repo,
        CacheService cache,
        ElasticService elastic,
        SearchCacheGeneration generation,
        CacheOptions cacheOptions,
        IndexingQueue indexingQueue,
        ILogger<JobsService> logger)
    {
        _repo = repo;
        _cache = cache;
        _elastic = elastic;
        _generation = generation;
        _cacheOptions = cacheOptions;
        _indexingQueue = indexingQueue;
        _logger = logger;
    }

    public async Task<Job> CreateAsync(ValidatedJob job, CancellationToken ct = default)
    {
        // Escrita confirmada no PostgreSQL ANTES de qualquer resposta de sucesso.
        // A indexação é eventual (outbox + worker): nunca bloqueia nem falha o POST.
        var created = await _repo.CreateAsync(Guid.NewGuid(), job, DateTime.UtcNow, ct);
        _indexingQueue.Wake(created.Id);
        return created;
    }

    public async Task<Job> ReplaceAsync(Guid id, ValidatedJob job, CancellationToken ct = default)
    {
        var updated = await _repo.ReplaceAsync(id, job, ct)
            ?? throw new JobNotFoundException(id);
        // Invalidação do cache de detalhe APÓS o commit — o valor antigo não volta.
        await _cache.DeleteKeyAsync(CacheKeys.DetailsKey(id), CacheCounters.Kind.Details);
        _indexingQueue.Wake(id);
        return updated;
    }

    /// <summary>Cache-aside no detalhe: hit → Redis; miss → PostgreSQL → popula cache (TTL configurável).</summary>
    public async Task<string> GetJobJsonAsync(Guid id, CancellationToken ct = default)
    {
        var key = CacheKeys.DetailsKey(id);
        var (outcome, cached) = await _cache.GetStringAsync(key, CacheCounters.Kind.Details);
        if (outcome == CacheOutcome.Hit && cached is not null)
            return cached;

        var job = await _repo.GetByIdAsync(id, ct) ?? throw new JobNotFoundException(id);

        var json = JsonSerializer.Serialize(JobResponse.From(job), ApiJson.Options);
        if (outcome == CacheOutcome.Miss)
            await _cache.SetStringAsync(key, json, TimeSpan.FromSeconds(_cacheOptions.DetailsTtlSeconds), CacheCounters.Kind.Details);
        // outcome == Error: Redis indisponível — responde pelo PostgreSQL, sem tentar popular.
        return json;
    }

    /// <summary>Busca pública: cache de resultado por chave canônica; sem cache → Elasticsearch.</summary>
    public async Task<string> SearchJsonAsync(SearchQueryParams p, CancellationToken ct = default)
    {
        var key = CacheKeys.SearchKey(_generation.Current, p);
        var (outcome, cached) = await _cache.GetStringAsync(key, CacheCounters.Kind.Search);
        if (outcome == CacheOutcome.Hit && cached is not null)
            return cached;

        var result = await _elastic.SearchAsync(p, ct); // 503 Problem Details se indisponível

        var response = new SearchResponse(
            p.Page,
            p.PageSize,
            result.Total,
            result.Items.Select(d => new SearchItemResponse(
                d.Id, d.Title, d.Company, d.Seniority, d.WorkMode, d.City,
                d.Technologies,
                ToDecimal(d.SalaryMin), ToDecimal(d.SalaryMax),
                DateTime.SpecifyKind(d.PublishedAt, DateTimeKind.Utc))).ToList());

        var json = JsonSerializer.Serialize(response, ApiJson.Options);
        if (outcome == CacheOutcome.Miss)
            await _cache.SetStringAsync(key, json, TimeSpan.FromSeconds(_cacheOptions.SearchTtlSeconds), CacheCounters.Kind.Search);
        return json;
    }

    public async Task<JobsStats> StatsAsync(CancellationToken ct = default) =>
        await _elastic.StatsAsync(ct);

    private static decimal? ToDecimal(double? value) =>
        value is null ? null : (decimal)value;
}
