using System.Text.Json.Serialization;

namespace TechJobs.Api.JobsCore;

/// <summary>
/// Corpo de POST/PUT. Campos de enum chegam como string e são validados manualmente
/// para produzir Problem Details legíveis (em vez do erro padrão de binding de JSON).
/// </summary>
public sealed class JobRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("company")]
    public string? Company { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("technologies")]
    public List<string?>? Technologies { get; init; }

    [JsonPropertyName("seniority")]
    public string? Seniority { get; init; }

    [JsonPropertyName("workMode")]
    public string? WorkMode { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("salaryMin")]
    public decimal? SalaryMin { get; init; }

    [JsonPropertyName("salaryMax")]
    public decimal? SalaryMax { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}
