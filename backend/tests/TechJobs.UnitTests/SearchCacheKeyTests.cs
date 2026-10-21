using Xunit;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;

namespace TechJobs.UnitTests;

public class SearchCacheKeyTests
{
    private static SearchQueryParams Params(
        string? q = null, string? seniority = null, string? workMode = null,
        string[]? technologies = null, decimal? min = null, decimal? max = null,
        int page = 1, int pageSize = 10, string sort = "relevance") =>
        new(q, seniority is null ? null : JobEnums.TryParseSeniority(seniority),
            workMode is null ? null : JobEnums.TryParseWorkMode(workMode),
            technologies ?? [], min, max, page, pageSize, sort);

    [Fact]
    public void Consultas_identicas_produzem_a_mesma_chave()
    {
        var a = Params(q: "desenvolvedor c#", seniority: "pleno", technologies: ["redis", "c#"], page: 2, sort: "newest");
        var b = Params(q: "desenvolvedor c#", seniority: "pleno", technologies: ["redis", "c#"], page: 2, sort: "newest");
        Assert.Equal(CacheKeys.CanonicalHash(a), CacheKeys.CanonicalHash(b));
        Assert.Equal(CacheKeys.SearchKey(7, a), CacheKeys.SearchKey(7, b));
    }

    [Fact]
    public void Ordem_das_tecnologias_nao_altera_a_chave_semantica_E()
    {
        var a = Params(technologies: ["redis", "c#", "docker"]);
        var b = Params(technologies: ["docker", "c#", "redis"]);
        Assert.Equal(CacheKeys.CanonicalHash(a), CacheKeys.CanonicalHash(b));
    }

    [Theory]
    [InlineData("page")]
    [InlineData("pageSize")]
    [InlineData("sort")]
    [InlineData("q")]
    [InlineData("seniority")]
    [InlineData("workMode")]
    [InlineData("minSalary")]
    [InlineData("maxSalary")]
    public void Variacoes_de_parametros_nao_colidem(string changed)
    {
        var a = Params(q: "dev", seniority: "pleno", workMode: "remoto", technologies: ["redis"], min: 100, max: 200, page: 1, pageSize: 10, sort: "relevance");
        var b = changed switch
        {
            "page" => a with { Page = 2 },
            "pageSize" => a with { PageSize = 20 },
            "sort" => a with { Sort = "newest" },
            "q" => a with { Q = "outra busca" },
            "seniority" => a with { Seniority = JobEnums.TryParseSeniority("senior") },
            "workMode" => a with { WorkMode = JobEnums.TryParseWorkMode("hibrido") },
            "minSalary" => a with { MinSalary = 300 },
            _ => a with { MaxSalary = 400 },
        };
        Assert.NotEqual(CacheKeys.CanonicalHash(a), CacheKeys.CanonicalHash(b));
    }

    [Fact]
    public void Tecnologia_a_mais_nao_colidem()
    {
        var a = Params(technologies: ["redis"]);
        var b = Params(technologies: ["redis", "docker"]);
        Assert.NotEqual(CacheKeys.CanonicalHash(a), CacheKeys.CanonicalHash(b));
    }

    [Fact]
    public void Q_normaliza_caixa_e_espacamento()
    {
        var a = Params(q: "Dev   Backend");
        var b = Params(q: "dev backend");
        Assert.Equal(CacheKeys.CanonicalHash(a), CacheKeys.CanonicalHash(b));
    }

    [Fact]
    public void Decimais_equivalentes_normalizam()
    {
        var a = Params(min: 6500.00m, max: 8500.50m);
        var b = Params(min: 6500m, max: 8500.5m);
        Assert.Equal(CacheKeys.CanonicalHash(a), CacheKeys.CanonicalHash(b));
    }

    [Fact]
    public void Chave_de_detalhe_e_versionada_por_id()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"job:details:v1:{id}", CacheKeys.DetailsKey(id));
        Assert.NotEqual(CacheKeys.DetailsKey(id), CacheKeys.DetailsKey(Guid.NewGuid()));
    }

    [Fact]
    public void Chave_de_busca_inclui_geracao()
    {
        var p = Params(q: "x");
        Assert.NotEqual(CacheKeys.SearchKey(1, p), CacheKeys.SearchKey(2, p));
    }
}
