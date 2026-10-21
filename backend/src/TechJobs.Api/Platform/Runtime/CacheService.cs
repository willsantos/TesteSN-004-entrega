using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TechJobs.Api.Platform.Runtime;

public enum CacheOutcome { Hit, Miss, Error }

/// <summary>
/// Acesso ao Redis com degradação graciosa: nenhuma falha do Redis (real ou simulada)
/// propaga para o chamador — a operação falha silenciosamente, é contada e o fluxo
/// segue pela fonte oficial (PostgreSQL/Elasticsearch).
/// </summary>
public sealed class CacheService
{
    private readonly IDatabase _db;
    private readonly FaultState _faults;
    private readonly CacheCounters _counters;
    private readonly ILogger<CacheService> _logger;

    public CacheService(IConnectionMultiplexer redis, FaultState faults, CacheCounters counters, ILogger<CacheService> logger)
    {
        _db = redis.GetDatabase();
        _faults = faults;
        _counters = counters;
        _logger = logger;
    }

    public async Task<(CacheOutcome Outcome, string? Value)> GetStringAsync(string key, CacheCounters.Kind kind)
    {
        var counter = _counters.For(kind);
        if (_faults.RedisDown)
        {
            counter.AddError();
            return (CacheOutcome.Error, null);
        }
        try
        {
            var value = await _db.StringGetAsync(key);
            if (value.HasValue)
            {
                counter.AddHit();
                return (CacheOutcome.Hit, value.ToString());
            }
            counter.AddMiss();
            return (CacheOutcome.Miss, null);
        }
        catch (Exception ex)
        {
            counter.AddError();
            LogRedisFailure("GET", key, ex);
            return (CacheOutcome.Error, null);
        }
    }

    public async Task<bool> SetStringAsync(string key, string value, TimeSpan ttl, CacheCounters.Kind kind)
    {
        var counter = _counters.For(kind);
        if (_faults.RedisDown)
        {
            counter.AddError();
            return false;
        }
        try
        {
            await _db.StringSetAsync(key, value, ttl);
            return true;
        }
        catch (Exception ex)
        {
            counter.AddError();
            LogRedisFailure("SET", key, ex);
            return false;
        }
    }

    /// <summary>Invalidação da chave de detalhe após commit do PUT (best-effort, contabilizada).</summary>
    public async Task DeleteKeyAsync(string key, CacheCounters.Kind kind)
    {
        var counter = _counters.For(kind);
        if (_faults.RedisDown)
        {
            counter.AddError();
            return;
        }
        try
        {
            await _db.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            counter.AddError();
            LogRedisFailure("DEL", key, ex);
        }
    }

    /// <summary>Limpeza total (reset/seed). Best-effort: falha não conta nos contadores de details/search.</summary>
    public async Task FlushAllAsync()
    {
        try
        {
            var endpoint = _db.Multiplexer.GetEndPoints().FirstOrDefault();
            if (endpoint is not null)
                await _db.Multiplexer.GetServer(endpoint).FlushDatabaseAsync(_db.Database);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flush do Redis falhou (best-effort).");
        }
    }

    private void LogRedisFailure(string op, string key, Exception ex) =>
        _logger.LogWarning("Redis {Op} falhou para a chave {Key}: {MessageType}. Fluxo segue degradado.",
            op, key, ex.GetType().Name);
}
