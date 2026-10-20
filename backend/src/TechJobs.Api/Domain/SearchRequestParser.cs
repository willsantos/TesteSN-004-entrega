using Microsoft.Extensions.Primitives;

namespace TechJobs.Api.Domain;

/// <summary>Converte a query string de /api/jobs/search em parâmetros validados (400 em entrada inválida).</summary>
public static class SearchRequestParser
{
    public static SearchQueryParams Parse(IEnumerable<KeyValuePair<string, StringValues>> query, out IReadOnlyList<string> errors)
    {
        var q = query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        string? text = null;
        if (q.TryGetValue("q", out var rawQ) && !string.IsNullOrWhiteSpace(rawQ))
            text = NormalizeQueryText(rawQ);

        Seniority? seniority = null;
        if (q.TryGetValue("seniority", out var rawSe) && !string.IsNullOrWhiteSpace(rawSe))
        {
            seniority = JobEnums.TryParseSeniority(rawSe);
            if (seniority is null)
                list.Add($"seniority (\"{rawSe.Trim()}\") é inválido: use junior, pleno ou senior.");
        }

        WorkMode? workMode = null;
        if (q.TryGetValue("workMode", out var rawWm) && !string.IsNullOrWhiteSpace(rawWm))
        {
            workMode = JobEnums.TryParseWorkMode(rawWm);
            if (workMode is null)
                list.Add($"workMode (\"{rawWm.Trim()}\") é inválido: use remoto, hibrido ou presencial.");
        }

        var technologies = new List<string>();
        if (q.TryGetValue("technologies", out var rawTech) && !string.IsNullOrWhiteSpace(rawTech))
        {
            foreach (var part in rawTech.Split(','))
            {
                var tech = part.Trim().ToLowerInvariant();
                if (tech.Length > 0 && !technologies.Contains(tech))
                    technologies.Add(tech);
            }
        }

        decimal? minSalary = ParseSalary(q, "minSalary", list);
        decimal? maxSalary = ParseSalary(q, "maxSalary", list);

        var page = ParsePositiveInt(q, "page", defaultVaue: 1, minimum: 1, errors: list,
            invalid: "page deve ser um inteiro maior ou igual a 1.");

        var pageSize = ParsePositiveInt(q, "pageSize", defaultVaue: 10, minimum: 1, errors: list,
            invalid: "pageSize deve ser um inteiro entre 1 e 50.");
        if (pageSize is > 50)
        {
            list.Add("pageSize deve ser um inteiro entre 1 e 50.");
            pageSize = 10;
        }

        var sort = SearchQueryParams.SortRelevance;
        if (q.TryGetValue("sort", out var rawSort) && !string.IsNullOrWhiteSpace(rawSort))
        {
            var s = rawSort.Trim().ToLowerInvariant();
            if (s is SearchQueryParams.SortRelevance or SearchQueryParams.SortNewest or SearchQueryParams.SortSalaryDesc)
                sort = s;
            else
                list.Add($"sort (\"{rawSort.Trim()}\") é inválido: use relevance, newest ou salary_desc.");
        }

        errors = list;
        return new SearchQueryParams(text, seniority, workMode, technologies, minSalary, maxSalary, page, pageSize, sort);
    }

    /// <summary>Trim + colapsa espaços internos + lowercase (a busca é case-insensitive por analyzer).</summary>
    public static string NormalizeQueryText(string value)
    {
        var collapsed = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.ToLowerInvariant();
    }

    private static decimal? ParseSalary(Dictionary<string, string> q, string field, List<string> errors)
    {
        if (!q.TryGetValue(field, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        if (!decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            errors.Add($"{field} (\"{raw.Trim()}\") deve ser um número decimal (use ponto como separador decimal).");
            return null;
        }
        if (value < 0)
        {
            errors.Add($"{field} ({value}) não pode ser negativo.");
            return null;
        }
        return value;
    }

    private static int ParsePositiveInt(Dictionary<string, string> q, string field, int defaultVaue, int minimum, List<string> errors, string invalid)
    {
        if (!q.TryGetValue(field, out var raw) || string.IsNullOrWhiteSpace(raw)) return defaultVaue;
        if (!int.TryParse(raw.Trim(), out var value) || value < minimum)
        {
            errors.Add(invalid);
            return defaultVaue;
        }
        return value;
    }
}
