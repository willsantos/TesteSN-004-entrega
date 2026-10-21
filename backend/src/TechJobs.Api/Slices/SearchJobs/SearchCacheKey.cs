using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechJobs.Api.Contracts;

namespace TechJobs.Api.Slices.SearchJobs;

/// <summary>
/// Chave de cache da busca. A chave deriva de uma forma canônica (sha256) da consulta
/// normalizada: consultas semanticamente idênticas reutilizam a chave; filtros, páginas
/// e ordenações diferentes nunca colidem.
/// </summary>
public static class SearchCacheKey
{
    public static string SearchKey(int generation, SearchQueryParams p) =>
        $"job:search:v1:{generation}:{CanonicalHash(p)}";

    /// <summary>
    /// Representação canônica estável (JSON de objeto anônimo com ordem de propriedades fixa).
    /// Normalizações: q (trim/espaçamento/lowercase), tecnologias (lowercase, dedup, ordem
    /// alfabética — a semântica é E, logo a ordem não altera o resultado), decimais sem
    /// zeros à direita, enums em string.
    /// </summary>
    public static string CanonicalQuery(SearchQueryParams p)
    {
        var canonical = new
        {
            q = p.Q is null ? null : SearchRequestParser.NormalizeQueryText(p.Q),
            seniority = p.Seniority?.ToString().ToLowerInvariant(),
            workMode = p.WorkMode?.ToString().ToLowerInvariant(),
            technologies = p.Technologies.OrderBy(t => t, StringComparer.Ordinal).ToList(),
            minSalary = p.MinSalary is null ? null : NormalizeDecimal(p.MinSalary.Value),
            maxSalary = p.MaxSalary is null ? null : NormalizeDecimal(p.MaxSalary.Value),
            page = p.Page,
            pageSize = p.PageSize,
            sort = p.Sort,
        };
        return JsonSerializer.Serialize(canonical, ApiJson.Options);
    }

    public static string CanonicalHash(SearchQueryParams p)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalQuery(p)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string NormalizeDecimal(decimal value) =>
        value.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
}
