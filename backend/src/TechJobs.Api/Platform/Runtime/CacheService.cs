using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TechJobs.Api.Platform.Runtime;

public enum CacheOutcome { Hit, Miss, Error }

/// <summary>
/// Acesso ao Redis com degradação graciosa: nenhuma falha do Redis (real ou simulada)
/// propaga para o chamador — a operação falha silenciosamente, é contada e o fluxo
/// segue pela fonte oficial (PostgreSQL/Elasticsearch).
/// Invalidações (DEL) que falharem — inclusive por queda simulada — ficam pendentes em
/// fila e são drenadas na primeira operação bem-sucedida: um PUT durante a queda do
/// Redis não deixa o valor antigo sobreviver além da primeira requisição após a
/// recuperação.
/// </summary>
public sealed class CacheService
{
    private readonly IDatabase _db;
    private readonly FaultState _faults;
    private readonly CacheCounters _counters;
    private readonly ILogger<CacheService> _logger;
    private readonly ConcurrentQueue<string> _pendingDeletes = new();
    private readonly ConcurrentDictionary<string, byte> _pendingSet = new();

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
            await DrainPendingDeletesAsync(kind);
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
            await DrainPendingDeletesAsync(kind);
            return true;
        }
        catch (Exception ex)
        {
            counter.AddError();
            LogRedisFailure("SET", key, ex);
            return false;
        }
    }

    /// <summary>
    /// Invalidação da chave de detalhe após commit do PUT (best-effort, contabilizada).
    /// Falha (exceção real ou queda simulada) enfileira a chave como delete pendente:
    /// a primeira operação bem-sucedida drena a fila.
    /// </summary>
    public async Task DeleteKeyAsync(string key, CacheCounters.Kind kind)
    {
        if (_faults.RedisDown)
        {
            _counters.For(kind).AddError();
            EnqueuePendingDelete(key);
            return;
        }
        if (!await DeleteCoreAsync(key, kind))
        {
            EnqueuePendingDelete(key);
            return;
        }
        await DrainPendingDeletesAsync(kind);
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

    // ---- deletes pendentes (PUT durante queda do Redis) ----

    private async Task<bool> DeleteCoreAsync(string key, CacheCounters.Kind kind)
    {
        try
        {
            await _db.KeyDeleteAsync(key);
            return true;
        }
        catch (Exception ex)
        {
            _counters.For(kind).AddError();
            LogRedisFailure("DEL", key, ex);
            return false;
        }
    }

    /// <summary>Enfileira sem duplicar: a guarda (_pendingSet) evita a mesma chave duas vezes na fila.</summary>
    private void EnqueuePendingDelete(string key)
    {
        if (_pendingSet.TryAdd(key, 0))
            _pendingDeletes.Enqueue(key);
    }

    /// <summary>
    /// Drena deletes pendentes (best-effort, sem lançar). Sem tails: se o Redis falhar
    /// de novo no meio da drenagem, a chave volta para a fila e a drenagem para —
    /// fica para a próxima operação bem-sucedida.
    /// </summary>
    private async Task DrainPendingDeletesAsync(CacheCounters.Kind kind)
    {
        while (_pendingDeletes.TryDequeue(out var pendingKey))
        {
            _pendingSet.TryRemove(pendingKey, out _);
            if (!await DeleteCoreAsync(pendingKey, kind))
            {
                EnqueuePendingDelete(pendingKey);
                break;
            }
        }
    }

    private void LogRedisFailure(string op, string key, Exception ex) =>
        _logger.LogWarning("Redis {Op} falhou para a chave {Key}: {MessageType}. Fluxo segue degradado.",
            op, key, ex.GetType().Name);
}
