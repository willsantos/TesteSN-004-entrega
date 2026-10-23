namespace TechJobs.Api.JobsCore;

/// <summary>
/// Validação e normalização do payload de vagas (regras da seção 4 do README).
/// Strings são aparadas (trim); tecnologias passam por dedup case-insensitive.
/// DECISÃO DE DESIGN (precisão salarial, seção 10 do spec): salários são normalizados
/// para 2 casas decimais com round half away from zero — a mesma escala e semântica do
/// numeric(14,2) do PostgreSQL. Assim o valor aceito no POST/PUT é exatamente o que é
/// persistido, o que o GET devolve e o que o índice de busca indexa (scaled_float,
/// scaling_factor 100). O limite de magnitude acompanha a coluna: 12 dígitos inteiros
/// + 2 decimais; acima disso a escrita estouraria o numeric(14,2).
/// </summary>
public static class JobValidator
{
    /// <summary>Máximo da coluna salary numeric(14,2): 999999999999.99 (12 inteiros + 2 decimais).</summary>
    private const decimal MaxSalary = 999_999_999_999.99m;
    /// <summary>Escala fixa dos salários, igual à precisão do numeric(14,2) do PostgreSQL.</summary>
    private const int SalaryScale = 2;

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
            list.Add($"description deve ter no mínimo 30 caracteres (recebido: {Received(request.Description)}).");

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

        // Negativos são validados no valor BRUTO (antes da normalização).
        if (request.SalaryMin is < 0)
            list.Add($"salaryMin ({request.SalaryMin}) não pode ser negativo.");
        if (request.SalaryMax is < 0)
            list.Add($"salaryMax ({request.SalaryMax}) não pode ser negativo.");
        if (request.SalaryMin > MaxSalary)
            list.Add($"salaryMin ({request.SalaryMin}) excede o valor máximo suportado (999999999999.99).");
        if (request.SalaryMax > MaxSalary)
            list.Add($"salaryMax ({request.SalaryMax}) excede o valor máximo suportado (999999999999.99).");

        // Faixa comparada sobre os valores NORMALIZADOS: 6500.006/6500.004 vira 6500.01/6500.00
        // (erro legítimo), mas 6500.004/6500.006 vira 6500.00/6500.01 (sem falso erro).
        var salaryMin = NormalizeSalary(request.SalaryMin);
        var salaryMax = NormalizeSalary(request.SalaryMax);
        if (salaryMin is not null && salaryMax is not null && salaryMax < salaryMin)
            list.Add($"salaryMax ({request.SalaryMax}) deve ser maior ou igual a salaryMin ({request.SalaryMin}).");

        errors = list;
        if (list.Count > 0) return null;

        return new ValidatedJob(
            title, company, description, technologies,
            seniority!.Value, workMode!.Value, city,
            salaryMin, salaryMax, status!.Value);
    }

    /// <summary>
    /// Normaliza o salário para 2 casas decimais (round half away from zero, igual ao
    /// numeric(14,2) do PostgreSQL): o valor ofertado passa a coincidir com o persistido.
    /// </summary>
    private static decimal? NormalizeSalary(decimal? value) =>
        value is null ? null : Math.Round(value.Value, SalaryScale, MidpointRounding.AwayFromZero);

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

    /// <summary>
    /// Campos de texto livre (title/company/description) ecoam apenas o COMPRIMENTO
    /// validado (pós-trim), nunca o CONTEÚDO: o payload vem do usuário e não deve
    /// vazar para logs/respostas. Enums controlados continuam ecoando o valor (Quote).
    /// </summary>
    private static string Received(string? raw)
    {
        var v = (raw ?? "").Trim();
        return v.Length == 0 ? "vazio" : $"{v.Length} caracteres";
    }

    private static string LengthMessage(string field, string? received, string range) =>
        $"{field} é obrigatório e deve ter entre {range} (recebido: {Received(received)}).";

    private static string Quote(string? value)
    {
        var v = (value ?? "").Trim();
        return v.Length == 0 ? "vazio" : $"\"{Truncate(v, 40)}\"";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
