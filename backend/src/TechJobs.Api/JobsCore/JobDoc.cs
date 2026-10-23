using System.Text.Json.Serialization;

namespace TechJobs.Api.JobsCore;

/// <summary>
/// Projeção de busca (nunca fonte da verdade). Nomes de campo fixados com
/// [JsonPropertyName] para garantir exatidão entre mapping, documento e queries.
/// Salários são armazenados como double (JSON numérico): é uma cópia para busca/filtro —
/// o valor decimal autoritativo vive no PostgreSQL (numeric(14,2)). No índice, o mapping
/// usa scaled_float com scaling_factor 100 (escala 2 definida): os centavos são exatos,
/// coerente com a normalização de 2 casas feita pelo JobValidator.
/// </summary>
public sealed class JobDoc
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("company")]
    public string Company { get; set; } = "";

    /// <summary>Grafia original (exibição + agregação terms para /api/jobs/stats).</summary>
    [JsonPropertyName("technologies")]
    public IReadOnlyList<string> Technologies { get; set; } = [];

    /// <summary>Versão lowercase para filtro case-insensitive (term em keyword é case-sensitive).</summary>
    [JsonPropertyName("techKeys")]
    public IReadOnlyList<string> TechKeys { get; set; } = [];

    [JsonPropertyName("seniority")]
    public string Seniority { get; set; } = "";

    [JsonPropertyName("workMode")]
    public string WorkMode { get; set; } = "";

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("salaryMin")]
    public double? SalaryMin { get; set; }

    [JsonPropertyName("salaryMax")]
    public double? SalaryMax { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("publishedAt")]
    public DateTime PublishedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    public static JobDoc From(Job job) => new()
    {
        Id = job.Id,
        Title = job.Title,
        Description = job.Description,
        Company = job.Company,
        Technologies = job.Technologies,
        TechKeys = job.Technologies.Select(t => t.Trim().ToLowerInvariant()).ToList(),
        Seniority = job.Seniority.ToApi(),
        WorkMode = job.WorkMode.ToApi(),
        City = job.City,
        SalaryMin = (double?)job.SalaryMin,
        SalaryMax = (double?)job.SalaryMax,
        Status = job.Status.ToApi(),
        PublishedAt = job.PublishedAt,
        UpdatedAt = job.UpdatedAt,
    };
}
