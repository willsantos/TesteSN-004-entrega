using Xunit;
using Microsoft.Extensions.Primitives;
using TechJobs.Api.Contracts;
using TechJobs.Api.Operations;
using TechJobs.Api.JobsCore;
using TechJobs.Api.Slices.SearchJobs;

namespace TechJobs.UnitTests;

public class SearchRequestParserTests
{
    private static SearchQueryParams Parse(string queryString)
    {
        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p =>
            {
                var kv = p.Split('=', 2);
                return new KeyValuePair<string, StringValues>(kv[0], kv.Length == 2 ? UrlDecode(kv[1]) : "");
            });
        return SearchRequestParser.Parse(pairs, out _);
    }

    private static string UrlDecode(string value) =>
        System.Uri.UnescapeDataString(value.Replace('+', ' '));

    private static IReadOnlyList<string> ErrorsOf(string queryString)
    {
        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var kv = p.Split('=', 2);
                return new KeyValuePair<string, StringValues>(kv[0], kv.Length == 2 ? UrlDecode(kv[1]) : "");
            });
        SearchRequestParser.Parse(pairs, out var errors);
        return errors;
    }

    [Fact]
    public void Defaults_padrao_do_contrato()
    {
        var p = Parse("");
        Assert.Equal(1, p.Page);
        Assert.Equal(10, p.PageSize);
        Assert.Equal(SearchQueryParams.SortRelevance, p.Sort);
        Assert.Null(p.Q);
        Assert.Empty(p.Technologies);
        // relevance sem q degrada para newest
        Assert.Equal(SearchQueryParams.SortNewest, p.EffectiveSort);
    }

    [Fact]
    public void Technologies_csv_trim_lowercase_e_dedup()
    {
        var p = Parse("technologies=Redis,%20C%23,redis");
        Assert.Equal(["redis", "c#"], p.Technologies);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=abc")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=51")]
    [InlineData("pageSize=abc")]
    [InlineData("minSalary=-1")]
    [InlineData("maxSalary=-5.5")]
    [InlineData("minSalary=abc")]
    [InlineData("seniority=guru")]
    [InlineData("workMode=presencial-remoto")]
    [InlineData("sort=aleatorio")]
    public void Parametros_invalidos_geram_erro(string query)
    {
        Assert.NotEmpty(ErrorsOf(query));
    }

    [Fact]
    public void Parametros_validos_nao_geram_erro()
    {
        Assert.Empty(ErrorsOf("q=dev backend&seniority=pleno&workMode=remoto&technologies=redis,c%23&minSalary=1000.50&maxSalary=9000&page=3&pageSize=25&sort=salary_desc"));
    }

    [Fact]
    public void Salarios_decimais_parse_invariant()
    {
        var p = Parse("minSalary=8000.75&maxSalary=12000");
        Assert.Equal(8000.75m, p.MinSalary);
        Assert.Equal(12000m, p.MaxSalary);
    }
}

public class SeedGeneratorTests
{
    [Fact]
    public void Mesmo_count_e_seed_produzem_conteudo_identico()
    {
        var a = SeedGenerator.Generate(250, 42);
        var b = SeedGenerator.Generate(250, 42);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Id, b[i].Id);
            Assert.Equal(a[i].Title, b[i].Title);
            Assert.Equal(a[i].Company, b[i].Company);
            Assert.Equal(a[i].Description, b[i].Description);
            Assert.Equal(a[i].Technologies, b[i].Technologies);
            Assert.Equal(a[i].SalaryMin, b[i].SalaryMin);
            Assert.Equal(a[i].Status, b[i].Status);
            Assert.Equal(a[i].PublishedAt, b[i].PublishedAt);
        }
    }

    [Fact]
    public void Seeds_diferentes_produzem_bases_diferentes()
    {
        var a = SeedGenerator.Generate(250, 1);
        var b = SeedGenerator.Generate(250, 2);
        Assert.NotEqual(a.Select(j => j.Title), b.Select(j => j.Title));
    }

    [Fact]
    public void Seed_respeita_regras_de_dominio()
    {
        var jobs = SeedGenerator.Generate(500, 7);
        Assert.All(jobs, j =>
        {
            Assert.InRange(j.Title.Length, 5, 160);
            Assert.InRange(j.Company.Length, 1, 120);
            Assert.True(j.Description.Length >= 30);
            Assert.NotEmpty(j.Technologies);
            Assert.Equal(j.Technologies.Distinct(StringComparer.OrdinalIgnoreCase).Count(), j.Technologies.Count);
            if (j.SalaryMin is not null && j.SalaryMax is not null)
                Assert.True(j.SalaryMax >= j.SalaryMin);
        });
        // Distribuição variada exigida: senioridade, modalidade, encerradas e sem salário
        Assert.Contains(jobs, j => j.Status == JobStatus.Encerrada);
        Assert.Contains(jobs, j => j.Status == JobStatus.Aberta);
        Assert.Contains(jobs, j => j.SalaryMin is null);
        Assert.Contains(jobs, j => j.SalaryMin is not null);
        Assert.Contains(jobs, j => j.City is null);
        Assert.Contains(jobs, j => j.City is not null);
        Assert.Equal(3, jobs.Select(j => j.Seniority).Distinct().Count());
        Assert.Equal(3, jobs.Select(j => j.WorkMode).Distinct().Count());
    }

    [Fact]
    public void Seed_com_5000_e_deterministico_tambem()
    {
        var a = SeedGenerator.Generate(5000, 42);
        var b = SeedGenerator.Generate(5000, 42);
        Assert.Equal(a.Select(j => j.Id), b.Select(j => j.Id));
    }
}
