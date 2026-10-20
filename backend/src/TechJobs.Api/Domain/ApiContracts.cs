namespace TechJobs.Api.Domain;

/// <summary>Parâmetros de busca já validados (entrada da busca e da chave de cache canônica).</summary>
public sealed record SearchQueryParams(
    string? Q,
    Seniority? Seniority,
    WorkMode? WorkMode,
    IReadOnlyList<string> Technologies,
    decimal? MinSalary,
    decimal? MaxSalary,
    int Page,
    int PageSize,
    string Sort)
{
    public const string SortRelevance = "relevance";
    public const string SortNewest = "newest";
    public const string SortSalaryDesc = "salary_desc";

    public bool HasQueryText => !string.IsNullOrWhiteSpace(Q);

    /// <summary>Ordenação efetiva: relevance sem `q` degrada para newest (documentado no README).</summary>
    public string EffectiveSort => Sort == SortRelevance && !HasQueryText ? SortNewest : Sort;
}

public sealed record JobResponse(
    Guid Id,
    string Title,
    string Company,
    string Description,
    IReadOnlyList<string> Technologies,
    string Seniority,
    string WorkMode,
    string? City,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string Status,
    DateTime PublishedAt,
    DateTime UpdatedAt)
{
    public static JobResponse From(Job job) => new(
        job.Id, job.Title, job.Company, job.Description, job.Technologies,
        job.Seniority.ToApi(), job.WorkMode.ToApi(), job.City,
        job.SalaryMin, job.SalaryMax, job.Status.ToApi(), job.PublishedAt, job.UpdatedAt);
}

public sealed record SearchItemResponse(
    Guid Id,
    string Title,
    string Company,
    string Seniority,
    string WorkMode,
    string? City,
    IReadOnlyList<string> Technologies,
    decimal? SalaryMin,
    decimal? SalaryMax,
    DateTime PublishedAt);

public sealed record SearchResponse(
    int Page,
    int PageSize,
    long Total,
    IReadOnlyList<SearchItemResponse> Items);

public sealed record TechnologyCount(string Name, long Count);

public sealed record JobsStats(
    long TotalOpenJobs,
    Dictionary<string, long> BySeniority,
    Dictionary<string, long> ByWorkMode,
    IReadOnlyList<TechnologyCount> TopTechnologies);

public sealed record CacheStatsConfig(int DetailsTtlSeconds, int SearchTtlSeconds);

public sealed record CounterSnapshot(long Hit, long Miss, long Error);

public sealed record CacheStatsResponse(CacheStatsConfig Config, CounterSnapshot Details, CounterSnapshot Search);

public static class ApiJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);
}
