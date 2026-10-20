using Xunit;
using TechJobs.Api.Domain;

namespace TechJobs.UnitTests;

public class JobValidatorTests
{
    private static JobRequest Valid() => new()
    {
        Title = "Desenvolvedor(a) Backend .NET Pleno",
        Company = "Acme Tech",
        Description = "Desenvolvimento de APIs e servicos com C#, PostgreSQL, Redis e testes automatizados.",
        Technologies = ["C#", ".NET", "Redis"],
        Seniority = "pleno",
        WorkMode = "remoto",
        City = null,
        SalaryMin = 6500,
        SalaryMax = 8500,
        Status = "aberta",
    };

    private static ValidatedJob ValidateOk(JobRequest request)
    {
        var validated = JobValidator.Validate(request, out var errors);
        Assert.True(validated is not null, "esperado válido, erros: " + string.Join(' ', errors));
        return validated!;
    }

    [Fact]
    public void Payload_valido_normaliza_e_passa()
    {
        var v = ValidateOk(Valid());
        Assert.Equal("Desenvolvedor(a) Backend .NET Pleno", v.Title);
        Assert.Equal(Seniority.Pleno, v.Seniority);
        Assert.Equal(WorkMode.Remoto, v.WorkMode);
        Assert.Equal(JobStatus.Aberta, v.Status);
        Assert.Equal(6500m, v.SalaryMin);
    }

    [Theory]
    [InlineData("Dev", "title")]                      // < 5
    [InlineData("", "title")]
    [InlineData("", "company")]
    [InlineData("Uma descrição curta", "description")] // < 30 após trim
    public void Campos_invalidos_rejeitados(string? value, string field)
    {
        var r = field switch
        {
            "title" => new JobRequest { Title = value, Company = "Acme", Description = new string('d', 40), Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto" },
            "company" => new JobRequest { Title = "Título válido", Company = value!, Description = new string('d', 40), Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto" },
            _ => new JobRequest { Title = "Título válido", Company = "Acme", Description = value, Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto" },
        };
        var validated = JobValidator.Validate(r, out var errors);
        Assert.Null(validated);
        Assert.Contains(errors, e => e.Contains(field));
    }

    [Fact]
    public void Title_acima_de_160_caracteres_rejeitado()
    {
        var r = new JobRequest
        {
            Title = new string('t', 161), Company = "Acme", Description = new string('d', 40),
            Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto",
        };
        Assert.Null(JobValidator.Validate(r, out var errors));
        Assert.Contains(errors, e => e.Contains("title"));
    }

    [Fact]
    public void Tecnologias_deduplicam_case_insensitive_preservando_primeira_ocorrencia()
    {
        var r = new JobRequest
        {
            Title = "Título válido",
            Company = "Acme",
            Description = new string('d', 40),
            Technologies = ["Redis", "redis", " REdis ", "postgres", ""],
            Seniority = "pleno",
            WorkMode = "remoto",
        };
        var v = ValidateOk(r);
        Assert.Equal(["Redis", "postgres"], v.Technologies);
    }

    [Fact]
    public void Tecnologias_vazias_rejeitadas()
    {
        var r = new JobRequest
        {
            Title = "Título válido", Company = "Acme", Description = new string('d', 40),
            Technologies = [], Seniority = "pleno", WorkMode = "remoto",
        };
        var validated = JobValidator.Validate(r, out var errors);
        Assert.Null(validated);
        Assert.Contains(errors, e => e.Contains("technologies"));
    }

    [Theory]
    [InlineData("guru", "seniority")]
    [InlineData("hibridoo", "workMode")]
    [InlineData("pausada", "status")]
    public void Enums_invalidos_rejeitados(string value, string field)
    {
        var r = Valid();
        var req = field switch
        {
            "seniority" => new JobRequest { Title = r.Title!, Company = r.Company!, Description = r.Description!, Technologies = ["x"], WorkMode = "remoto", Seniority = value },
            "workMode" => new JobRequest { Title = r.Title!, Company = r.Company!, Description = r.Description!, Technologies = ["x"], Seniority = "pleno", WorkMode = value },
            _ => new JobRequest { Title = r.Title!, Company = r.Company!, Description = r.Description!, Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto", Status = value },
        };
        var validated = JobValidator.Validate(req, out var errors);
        Assert.Null(validated);
        Assert.Contains(errors, e => e.Contains(field));
    }

    [Fact]
    public void Enums_case_insensitive_sao_aceitos()
    {
        var v = ValidateOk(new JobRequest
        {
            Title = "Título válido", Company = "Acme", Description = new string('d', 40),
            Technologies = ["x"], Seniority = "PLENO", WorkMode = "Remoto", Status = "Aberta",
        });
        Assert.Equal(Seniority.Pleno, v.Seniority);
        Assert.Equal(WorkMode.Remoto, v.WorkMode);
        Assert.Equal(JobStatus.Aberta, v.Status);
    }

    [Fact]
    public void Status_ausente_default_aberta()
    {
        var v = ValidateOk(new JobRequest
        {
            Title = "Título válido", Company = "Acme", Description = new string('d', 40),
            Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto",
        });
        Assert.Equal(JobStatus.Aberta, v.Status);
    }

    [Fact]
    public void Salario_negativo_rejeitado()
    {
        var req = new JobRequest
        {
            Title = "Título válido", Company = "Acme", Description = new string('d', 40),
            Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto",
            SalaryMin = -1,
        };
        var validated = JobValidator.Validate(req, out var errors);
        Assert.Null(validated);
        Assert.Contains(errors, e => e.Contains("salaryMin") && e.Contains("negativo"));
    }

    [Fact]
    public void SalaryMax_menor_que_salaryMin_rejeitado_com_mensagem_do_contrato()
    {
        var req = new JobRequest
        {
            Title = "Título válido", Company = "Acme", Description = new string('d', 40),
            Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto",
            SalaryMin = 6500, SalaryMax = 5000,
        };
        var validated = JobValidator.Validate(req, out var errors);
        Assert.Null(validated);
        Assert.Contains("salaryMax (5000) deve ser maior ou igual a salaryMin (6500).", errors);
    }

    [Fact]
    public void Faixa_apenas_min_ou_apenas_max_e_valida()
    {
        var onlyMin = ValidateOk(new JobRequest
        {
            Title = "Título válido", Company = "Acme", Description = new string('d', 40),
            Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto", SalaryMin = 100,
        });
        Assert.Equal(100m, onlyMin.SalaryMin);
        Assert.Null(onlyMin.SalaryMax);

        var none = ValidateOk(new JobRequest
        {
            Title = "Título válido", Company = "Acme", Description = new string('d', 40),
            Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto",
        });
        Assert.Null(none.SalaryMin);
        Assert.Null(none.SalaryMax);
    }

    [Fact]
    public void City_vazia_vira_null()
    {
        var v = ValidateOk(new JobRequest
        {
            Title = "Título válido", Company = "Acme", Description = new string('d', 40),
            Technologies = ["x"], Seniority = "pleno", WorkMode = "remoto", City = "   ",
        });
        Assert.Null(v.City);
    }

    [Fact]
    public void SameContentAs_ignora_timestamps_para_idempotencia()
    {
        var v = ValidateOk(Valid());
        var job = new Job(Guid.NewGuid(), v.Title, v.Company, v.Description, v.Technologies,
            v.Seniority, v.WorkMode, v.City, v.SalaryMin, v.SalaryMax, v.Status,
            DateTime.UtcNow, DateTime.UtcNow.AddDays(1));
        Assert.True(job.SameContentAs(v));

        var changed = v with { Title = "Outro título válido" };
        Assert.False(job.SameContentAs(changed));
    }
}
