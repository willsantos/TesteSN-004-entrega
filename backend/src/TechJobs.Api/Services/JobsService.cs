using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Services;

/// <summary>
/// Orquestra PostgreSQL (verdade) e Redis (cache) nos fluxos de escrita da API.
///leituras públicas vivem nos slices (Slices/*) e devolvem JSON pronto.
/// </summary>
public sealed class JobsService
{
    private readonly JobStore _repo;
    private readonly CacheService _cache;
    private readonly IndexingQueue _indexingQueue;

    public JobsService(
        JobStore repo,
        CacheService cache,
        IndexingQueue indexingQueue)
    {
        _repo = repo;
        _cache = cache;
        _indexingQueue = indexingQueue;
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
}
