namespace TechJobs.Api.Infrastructure;

/// <summary>
/// Estado de falhas simuladas (dev-only, ativado via POST /admin/faults).
/// Escritos são simples flags voláteis: leitura/escrita atômicas, sem lock.
/// </summary>
public sealed class FaultState
{
    private volatile bool _redisDown;
    private volatile bool _elasticDown;

    public bool RedisDown => _redisDown;
    public bool ElasticDown => _elasticDown;

    public void SetRedis(bool down) => _redisDown = down;
    public void SetElastic(bool down) => _elasticDown = down;
    public void Reset()
    {
        _redisDown = false;
        _elasticDown = false;
    }
}
