namespace TechJobs.Api.JobsCore;

/// <summary>Chave do cache de detalhe por id de vaga.</summary>
public static class CacheKeys
{
    public static string DetailsKey(Guid id) => $"job:details:v1:{id}";
}
