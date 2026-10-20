namespace TechJobs.Api.Infrastructure;

/// <summary>
/// Geração do cache de busca: cada invalidação (seed/reset/drain/reindex) incrementa o
/// contador, e a geração faz parte da chave — chaves antigas ficam imediatamente
/// inalcançáveis (invalidação lógica sem SCAN/DEL em massa) e expiram pelo TTL.
/// Nasce com o instante atual (unix seconds) para não colidir com chaves de execuções
/// anteriores que ainda estejam vivas no Redis.
/// </summary>
public sealed class SearchCacheGeneration
{
    private int _generation = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public int Current => Volatile.Read(ref _generation);

    public void Invalidate() => Interlocked.Increment(ref _generation);
}
