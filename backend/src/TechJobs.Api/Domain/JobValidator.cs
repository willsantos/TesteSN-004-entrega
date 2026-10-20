namespace TechJobs.Api.Domain;

/// <summary>
/// Validação e normalização do payload de vagas (regras da seção 4 do README).
/// Strings são aparadas (trim); tecnologias passam por dedup case-insensitive.
/// </summary>
public static class JobValidator
{
    public static ValidatedJob? Validate(JobRequest request, out IReadOnlyList<string> errors)
    {
        var list = new List<string>();

        var title = (request.Title ?? "").Trim();
        if (title.Length is < 5 or > 160)
            list.Add(LengthMessage("title", request.Title, "5 a 160 caracteres"));

        var company = (request.Company ?? "").Trim();
        if (company.Length is < 1 or > 120)
            list.Add(LengthMessage("company", request.Company, "1 a 120 caracteres"));

        var description = (request.Description ?? "").Trim();
        if (description.Length < 30)
            list.Add($"description deve ter no mínimo 30 caracteres (recebido: {Quote(request.Description)}).");

        var technologies = NormalizeTechnologies(request.Technologies);
        if (technologies.Count == 0)
            list.Add("technologies deve conter ao menos 1 tecnologia (strings não vazias, sem duplicatas após normalização).");

        var seniority = JobEnums.TryParseSeniority(request.Seniority);
        if (seniority is null)
            list.Add($"seniority ({Quote(request.Seniority)}) é inválido: use junior, pleno ou senior.");

        var workMode = JobEnums.TryParseWorkMode(request.WorkMode);
        if (workMode is null)
            list.Add($"workMode ({Quote(request.WorkMode)}) é inválido: use remoto, hibrido ou presencial.");

        var status = string.IsNullOrWhiteSpace(request.Status) ? JobStatus.Aberta : JobEnums.TryParseStatus(request.Status);
        if (status is null)
            list.Add($"status ({Quote(request.Status)}) é inválido: use aberta ou encerrada.");

        var city = string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim();

        if (request.SalaryMin is < 0)
            list.Add($"salaryMin ({request.SalaryMin}) não pode ser negativo.");
        if (request.SalaryMax is < 0)
            list.Add($"salaryMax ({request.SalaryMax}) não pode ser negativo.");
        if (request.SalaryMin is not null && request.SalaryMax is not null && request.SalaryMax < request.SalaryMin)
            list.Add($"salaryMax ({request.SalaryMax}) deve ser maior ou igual a salaryMin ({request.SalaryMin}).");

        errors = list;
        if (list.Count > 0) return null;

        return new ValidatedJob(
            title, company, description, technologies,
            seniority!.Value, workMode!.Value, city,
            request.SalaryMin, request.SalaryMax, status!.Value);
    }

    /// <summary>Trim + dedup case-insensitive preservando a primeira ocorrência (e sua grafia).</summary>
    public static IReadOnlyList<string> NormalizeTechnologies(IEnumerable<string?>? raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var item in raw ?? [])
        {
            if (item is null) continue;
            var tech = item.Trim();
            if (tech.Length == 0) continue;
            if (seen.Add(tech)) result.Add(tech);
        }
        return result;
    }

    private static string LengthMessage(string field, string? received, string range) =>
        $"{field} é obrigatório e deve ter entre {range} (recebido: {Quote(received)}).";

    private static string Quote(string? value)
    {
        var v = (value ?? "").Trim();
        return v.Length == 0 ? "vazio" : $"\"{Truncate(v, 40)}\"";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
