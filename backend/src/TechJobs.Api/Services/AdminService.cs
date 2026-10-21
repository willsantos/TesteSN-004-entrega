using Microsoft.Extensions.Logging;
using TechJobs.Api.Contracts;
using TechJobs.Api.Infrastructure;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Services;

/// <summary>Operações dos endpoints dev-only (ENABLE_TEST_ENDPOINTS=true) — mecanismo de teste determinístico.</summary>
public sealed class AdminService
{
    private readonly JobStore _repo;
    private readonly ElasticIndex _elastic;
    private readonly CacheService _cache;
    private readonly CacheCounters _counters;
    private readonly FaultState _faults;
    private readonly SearchCacheGeneration _generation;
    private readonly IndexingWorker _indexing;
    private readonly Outbox _outbox;
    private readonly ILogger<AdminService> _logger;

    public AdminService(
        JobStore repo,
        ElasticIndex elastic,
        CacheService cache,
        CacheCounters counters,
        FaultState faults,
        SearchCacheGeneration generation,
        IndexingWorker indexing,
        Outbox outbox,
        ILogger<AdminService> logger)
    {
        _repo = repo;
        _elastic = elastic;
        _cache = cache;
        _counters = counters;
        _faults = faults;
        _generation = generation;
        _indexing = indexing;
        _outbox = outbox;
        _logger = logger;
    }

    /// <summary>Limpa PostgreSQL, recria o índice vazio, limpa Redis, zera contadores, desliga falhas.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        _faults.Reset();
        await _repo.TruncateAsync(ct);
        await _elastic.DropAndCreateIndexAsync(ct);
        await _cache.FlushAllAsync();
        _counters.Reset();
        _generation.Invalidate();
    }

    /// <summary>
    /// Substitui toda a base por vagas sintéticas determinísticas. Síncrono: ao responder,
    /// PostgreSQL e índice já refletem a base e os caches estão limpos.
    /// </summary>
    public async Task<(int Count, int Seed)> SeedAsync(int count, int seed, CancellationToken ct = default)
    {
        var jobs = SeedGenerator.Generate(count, seed);

        await _repo.TruncateAsync(ct);
        await _repo.InsertManyAsync(jobs, ct);

        await _elastic.DeleteAllDocumentsAsync(ct);
        await _elastic.IndexManyAsync(jobs, ct);
        await _elastic.RefreshAsync(ct);

        await _cache.FlushAllAsync();
        _generation.Invalidate();
        return (count, seed);
    }

    /// <summary>
    /// Aplica indexação pendente, faz refresh do índice e invalida o cache de busca:
    /// ao retornar, toda escrita confirmada antes da chamada está visível na busca/stats.
    /// </summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        await _indexing.ProcessPendingAsync(ct);
        await _elastic.RefreshAsync(ct);
        _generation.Invalidate();
    }

    /// <summary>Reconstrói o índice a partir do PostgreSQL (ids estáveis → nunca duplica).</summary>
    public async Task<long> ReindexAsync(CancellationToken ct = default)
    {
        var jobs = await _repo.GetAllAsync(ct);
        await _elastic.DeleteAllDocumentsAsync(ct);
        await _elastic.IndexManyAsync(jobs, ct);
        await _elastic.RefreshAsync(ct);
        await _outbox.ClearOutboxAsync(ct);
        _generation.Invalidate();
        return jobs.Count;
    }

    public CacheStatsResponse GetCacheStats(CacheOptions options) => new(
        new CacheStatsConfig(options.DetailsTtlSeconds, options.SearchTtlSeconds),
        new CounterSnapshot(_counters.Details.Hit, _counters.Details.Miss, _counters.Details.Error),
        new CounterSnapshot(_counters.Search.Hit, _counters.Search.Miss, _counters.Search.Error));
}
