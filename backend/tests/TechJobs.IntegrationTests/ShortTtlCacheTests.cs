using System.Net;
using System.Text.Json;
using Xunit;

namespace TechJobs.IntegrationTests;

/// <summary>
/// Expiração REAL das chaves de cache contra a instância auxiliar com TTL de 2s
/// (TECHJOBS_API_SHORTTTL, porta 8086; admin disponível nela). Os testes de hit/miss
/// da ResilienceTests provam o cache-aside; aqui prova-se que a chave sai do Redis
/// quando o TTL vence — a leitura volta à fonte e o contador de miss sobe de novo.
/// </summary>
public class ShortTtlCacheTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var http = ApiClient.ForShortTtl();
        using var reset = await http.PostAsync("/admin/reset", null);
        Assert.True(reset.IsSuccessStatusCode);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> CacheStats(HttpClient http)
    {
        using var response = await http.GetAsync("/admin/cache-stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiClient.ReadJson(response);
    }

    private static object ValidJob(string title) => new
    {
        title,
        company = "Cache Ltda",
        description = "Descrição suficientemente longa para passar na validação do domínio de vagas.",
        technologies = new[] { "C#", "Redis" },
        seniority = "pleno",
        workMode = "remoto",
        city = (string?)null,
        salaryMin = 5000,
        salaryMax = 7000,
        status = "aberta",
    };

    [ShortTtlApiFact]
    public async Task Expiracao_real_do_cache_de_detalhe()
    {
        using var http = ApiClient.ForShortTtl();
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob("TTL Detalhe Expira Test")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString();

        // Contador ANTES da 1ª leitura
        var missBefore = (await CacheStats(http)).GetProperty("details").GetProperty("miss").GetInt64();

        // 1ª leitura: miss (PostgreSQL) + popula a chave com TTL de 2s
        using (var get1 = await http.GetAsync($"/api/jobs/{id}"))
            Assert.Equal(HttpStatusCode.OK, get1.StatusCode);
        // 2ª leitura: hit (a chave está viva no Redis)
        using (var get2 = await http.GetAsync($"/api/jobs/{id}"))
            Assert.Equal("TTL Detalhe Expira Test", (await ApiClient.ReadJson(get2)).GetProperty("title").GetString());
        var statsMid = await CacheStats(http);
        Assert.Equal(missBefore + 1, statsMid.GetProperty("details").GetProperty("miss").GetInt64());
        Assert.Equal(1, statsMid.GetProperty("details").GetProperty("hit").GetInt64());

        // TTL 2s + margem: a chave expira de verdade no Redis
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 3ª leitura: 200 com o mesmo payload, vindo da fonte outra vez (novo miss)
        using (var get3 = await http.GetAsync($"/api/jobs/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, get3.StatusCode);
            Assert.Equal("TTL Detalhe Expira Test", (await ApiClient.ReadJson(get3)).GetProperty("title").GetString());
        }
        var statsAfter = await CacheStats(http);
        Assert.True(
            statsAfter.GetProperty("details").GetProperty("miss").GetInt64() > statsMid.GetProperty("details").GetProperty("miss").GetInt64(),
            "details.miss deveria aumentar após a expiração do TTL (leitura voltou à fonte).");
    }

    [ShortTtlApiFact]
    public async Task Expiracao_real_do_cache_de_busca()
    {
        using var http = ApiClient.ForShortTtl();
        // Termo único: chave de busca exclusiva deste teste (resultado vazio também é cacheado)
        var termo = $"ttlsearch{Guid.NewGuid():N}";
        var rota = $"/api/jobs/search?q={termo}&pageSize=5";

        // Contador ANTES da 1ª busca
        var missBefore = (await CacheStats(http)).GetProperty("search").GetProperty("miss").GetInt64();

        // 1ª busca: miss (Elasticsearch) + popula a chave com TTL de 2s
        using (var s1 = await http.GetAsync(rota))
            Assert.Equal(HttpStatusCode.OK, s1.StatusCode);
        // 2ª busca idêntica: hit (a chave está viva no Redis)
        using (var s2 = await http.GetAsync(rota))
            Assert.Equal(HttpStatusCode.OK, s2.StatusCode);
        var statsMid = await CacheStats(http);
        Assert.Equal(missBefore + 1, statsMid.GetProperty("search").GetProperty("miss").GetInt64());
        Assert.Equal(1, statsMid.GetProperty("search").GetProperty("hit").GetInt64());

        // TTL 2s + margem: a chave expira de verdade no Redis
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 3ª busca: 200 e o contador de miss sobe de novo (chave expirada, volta ao Elasticsearch)
        using (var s3 = await http.GetAsync(rota))
            Assert.Equal(HttpStatusCode.OK, s3.StatusCode);
        var statsAfter = await CacheStats(http);
        Assert.True(
            statsAfter.GetProperty("search").GetProperty("miss").GetInt64() > statsMid.GetProperty("search").GetProperty("miss").GetInt64(),
            "search.miss deveria aumentar após a expiração do TTL (consulta voltou ao Elasticsearch).");
    }
}
