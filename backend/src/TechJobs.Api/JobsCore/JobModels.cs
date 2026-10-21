namespace TechJobs.Api.JobsCore;

public enum Seniority { Junior, Pleno, Senior }
public enum WorkMode { Remoto, Hibrido, Presencial }
public enum JobStatus { Aberta, Encerrada }

public static class JobEnums
{
    public static string ToApi(this Seniority s) => s switch
    {
        Seniority.Junior => "junior",
        Seniority.Pleno => "pleno",
        _ => "senior",
    };

    public static string ToApi(this WorkMode w) => w switch
    {
        WorkMode.Remoto => "remoto",
        WorkMode.Hibrido => "hibrido",
        _ => "presencial",
    };

    public static string ToApi(this JobStatus s) => s == JobStatus.Aberta ? "aberta" : "encerrada";

    public static Seniority? TryParseSeniority(string? value) => Normalize(value) switch
    {
        "junior" => Seniority.Junior,
        "pleno" => Seniority.Pleno,
        "senior" => Seniority.Senior,
        _ => null,
    };

    public static WorkMode? TryParseWorkMode(string? value) => Normalize(value) switch
    {
        "remoto" => WorkMode.Remoto,
        "hibrido" => WorkMode.Hibrido,
        "presencial" => WorkMode.Presencial,
        _ => null,
    };

    public static JobStatus? TryParseStatus(string? value) => Normalize(value) switch
    {
        "aberta" => JobStatus.Aberta,
        "encerrada" => JobStatus.Encerrada,
        _ => null,
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

/// <summary>Vaga persistida (fonte da verdade: PostgreSQL).</summary>
public sealed record Job(
    Guid Id,
    string Title,
    string Company,
    string Description,
    IReadOnlyList<string> Technologies,
    Seniority Seniority,
    WorkMode WorkMode,
    string? City,
    decimal? SalaryMin,
    decimal? SalaryMax,
    JobStatus Status,
    DateTime PublishedAt,
    DateTime UpdatedAt);

/// <summary>Payload já validado/normalizado pronto para persistir (sem id/timestamps).</summary>
public sealed record ValidatedJob(
    string Title,
    string Company,
    string Description,
    IReadOnlyList<string> Technologies,
    Seniority Seniority,
    WorkMode WorkMode,
    string? City,
    decimal? SalaryMin,
    decimal? SalaryMax,
    JobStatus Status);

public static class JobExtensions
{
    /// <summary>
    /// Compara apenas os campos de domínio (ignora id e timestamps). Usado para tornar o PUT
    /// idempotente de verdade: reaplicar o mesmo payload não altera updatedAt.
    /// </summary>
    public static bool SameContentAs(this Job job, ValidatedJob other) =>
        string.Equals(job.Title, other.Title, StringComparison.Ordinal)
        && string.Equals(job.Company, other.Company, StringComparison.Ordinal)
        && string.Equals(job.Description, other.Description, StringComparison.Ordinal)
        && job.Technologies.SequenceEqual(other.Technologies)
        && job.Seniority == other.Seniority
        && job.WorkMode == other.WorkMode
        && string.Equals(job.City, other.City, StringComparison.Ordinal)
        && job.SalaryMin == other.SalaryMin
        && job.SalaryMax == other.SalaryMax
        && job.Status == other.Status;
}
