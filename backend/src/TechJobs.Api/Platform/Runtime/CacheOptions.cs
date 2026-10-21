namespace TechJobs.Api.Platform.Runtime;

/// <summary>Configuração de cache por ambiente (Cache__DetailsTtlSeconds / Cache__SearchTtlSeconds).</summary>
public sealed record CacheOptions(int DetailsTtlSeconds, int SearchTtlSeconds);
