namespace TechJobs.Api.Domain;

/// <summary>Payload/filtro inválido → 400 Problem Details.</summary>
public sealed class ValidationException(string detail) : Exception(detail)
{
    public string Detail => detail;
}

/// <summary>Vaga não encontrada → 404 Problem Details.</summary>
public sealed class JobNotFoundException(Guid id) : Exception($"Vaga {id} não encontrada.")
{
    public Guid JobId => id;
}

/// <summary>Elasticsearch indisponível (falha real ou simulada) → 503 Problem Details na leitura.</summary>
public sealed class SearchUnavailableException(string detail) : Exception(detail)
{
    public string Detail => detail;
}
