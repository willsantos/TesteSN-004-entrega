using System.Net;
using System.Text.Json;
using Xunit;

namespace TechJobs.IntegrationTests;

/// <summary>Cache (hit/miss/invalidação), falhas simuladas, drain e reindexação.</summary>
public class ResilienceTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var http = ApiClient.Create();
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

    private static async Task SetFault(HttpClient http, string target, bool enabled)
    {
        using var response = await http.PostAsync("/admin/faults",
            ApiClient.JsonBody(new { target, enabled }));
        Assert.True(response.IsSuccessStatusCode);
        var body = await ApiClient.ReadJson(response);
        Assert.Equal(target, body.GetProperty("target").GetString());
        Assert.Equal(enabled, body.GetProperty("enabled").GetBoolean());
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

    [LiveApiFact]
    public async Task Detalhe_cacheaside_hit_miss_e_invalidacao_pos_PUT()
    {
        using var http = ApiClient.Create();
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob("Cache Hit Miss Test")));
        var id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString();

        // 1ª leitura: miss (PostgreSQL) + popula
        using (var get1 = await http.GetAsync($"/api/jobs/{id}"))
            Assert.Equal(HttpStatusCode.OK, get1.StatusCode);
        var stats1 = await CacheStats(http);
        Assert.Equal(0, stats1.GetProperty("details").GetProperty("hit").GetInt64());
        Assert.Equal(1, stats1.GetProperty("details").GetProperty("miss").GetInt64());

        // 2ª leitura: hit (Redis)
        using (var get2 = await http.GetAsync($"/api/jobs/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, get2.StatusCode);
            Assert.Equal("Cache Hit Miss Test", (await ApiClient.ReadJson(get2)).GetProperty("title").GetString());
        }
        var stats2 = await CacheStats(http);
        Assert.Equal(1, stats2.GetProperty("details").GetProperty("hit").GetInt64());

        // PUT invalida: nova leitura é miss e enxerga o dado novo
        var updated = ValidJob("Cache Hit Miss Test II");
        using (var put = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(updated)))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var get3 = await http.GetAsync($"/api/jobs/{id}"))
            Assert.Equal("Cache Hit Miss Test II", (await ApiClient.ReadJson(get3)).GetProperty("title").GetString());
        var stats3 = await CacheStats(http);
        Assert.Equal(2, stats3.GetProperty("details").GetProperty("miss").GetInt64());
    }

    [LiveApiFact]
    public async Task Cache_de_busca_reutiliza_resultado_identical()
    {
        using var http = ApiClient.Create();
        using (var seed = await http.PostAsync("/admin/seed", ApiClient.JsonBody(new { count = 50, seed = 7 })))
            Assert.True(seed.IsSuccessStatusCode);

        using (var s1 = await http.GetAsync("/api/jobs/search?q=desenvolvedor&pageSize=5"))
            Assert.Equal(HttpStatusCode.OK, s1.StatusCode);
        var stats1 = await CacheStats(http);
        Assert.Equal(1, stats1.GetProperty("search").GetProperty("miss").GetInt64());
        Assert.Equal(0, stats1.GetProperty("search").GetProperty("hit").GetInt64());

        // Consulta idêntica → hit, sem nova chamada ao Elasticsearch
        using (var s2 = await http.GetAsync("/api/jobs/search?q=desenvolvedor&pageSize=5"))
        {
            Assert.Equal(HttpStatusCode.OK, s2.StatusCode);
            var body = await ApiClient.ReadJson(s2);
            Assert.Equal(5, body.GetProperty("pageSize").GetInt32());
        }
        var stats2 = await CacheStats(http);
        Assert.Equal(1, stats2.GetProperty("search").GetProperty("hit").GetInt64());
        Assert.Equal(1, stats2.GetProperty("search").GetProperty("miss").GetInt64());

        // Parâmetros diferentes → miss (nova chave canônica)
        using (var s3 = await http.GetAsync("/api/jobs/search?q=desenvolvedor&pageSize=6"))
            Assert.Equal(HttpStatusCode.OK, s3.StatusCode);
        var stats3 = await CacheStats(http);
        Assert.Equal(2, stats3.GetProperty("search").GetProperty("miss").GetInt64());
    }

    [LiveApiFact]
    public async Task Falha_redis_degrada_sem_500_e_conta_erros()
    {
        using var http = ApiClient.Create();
        string id;
        {
            using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob("Falha Redis Test")));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString()!;
        }
        using (var drain = await http.PostAsync("/admin/indexing/drain", null))
            Assert.True(drain.IsSuccessStatusCode);

        await SetFault(http, "redis", true);

        // Detalhe continua 200 via PostgreSQL
        using (var get = await http.GetAsync($"/api/jobs/{id}"))
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        // Busca continua 200 via Elasticsearch (sem cache)
        using (var search = await http.GetAsync("/api/jobs/search?q=falha"))
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        var stats = await CacheStats(http);
        Assert.Equal(1, stats.GetProperty("details").GetProperty("error").GetInt64());
        Assert.Equal(1, stats.GetProperty("search").GetProperty("error").GetInt64());

        await SetFault(http, "redis", false);
        // Normalizado: volta a cachear
        using (var get2 = await http.GetAsync($"/api/jobs/{id}"))
            Assert.Equal(HttpStatusCode.OK, get2.StatusCode);
        var stats2 = await CacheStats(http);
        Assert.Equal(1, stats2.GetProperty("details").GetProperty("miss").GetInt64());
    }

    [LiveApiFact]
    public async Task Falha_elasticsearch_busca_503_escrita_persiste_e_drain_recupera()
    {
        using var http = ApiClient.Create();
        await SetFault(http, "elasticsearch", true);

        // Busca/stats → 503 Problem Details controlado
        using (var search = await http.GetAsync("/api/jobs/search?q=x"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, search.StatusCode);
            Assert.Equal("application/problem+json", search.Content.Headers.ContentType?.MediaType);
            var body = await ApiClient.ReadJson(search);
            Assert.Equal(503, body.GetProperty("status").GetInt32());
        }
        using (var stats = await http.GetAsync("/api/jobs/stats"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, stats.StatusCode);

        // Escrita NÃO pode falhar nem perder cadastro: 201 confirmado no PostgreSQL
        string id;
        {
            using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob("Escrita Com ES Fora")));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString()!;
        }

        await SetFault(http, "elasticsearch", false);
        using (var drain = await http.PostAsync("/admin/indexing/drain", null))
            Assert.True(drain.IsSuccessStatusCode);

        // Índice recuperado: vaga escrita durante a falha agora está visível
        using (var search = await http.GetAsync("/api/jobs/search?q=escrita"))
        {
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            var body = await ApiClient.ReadJson(search);
            Assert.Contains(body.GetProperty("items").EnumerateArray(),
                i => i.GetProperty("id").GetString() == id);
        }
    }

    [LiveApiFact]
    public async Task Reindex_reconstrui_sem_duplicar()
    {
        using var http = ApiClient.Create();
        using (var seed = await http.PostAsync("/admin/seed", ApiClient.JsonBody(new { count = 60, seed = 11 })))
            Assert.True(seed.IsSuccessStatusCode);

        long totalBefore;
        using (var search = await http.GetAsync("/api/jobs/search?pageSize=1"))
            totalBefore = (await ApiClient.ReadJson(search)).GetProperty("total").GetInt64();

        foreach (var _ in new[] { 1, 2 })
        {
            using var reindex = await http.PostAsync("/admin/indexing/reindex", null);
            Assert.True(reindex.IsSuccessStatusCode);
            var body = await ApiClient.ReadJson(reindex);
            Assert.Equal(60, body.GetProperty("count").GetInt64());
        }

        using (var search = await http.GetAsync("/api/jobs/search?pageSize=1"))
        {
            var totalAfter = (await ApiClient.ReadJson(search)).GetProperty("total").GetInt64();
            Assert.Equal(totalBefore, totalAfter);
        }
    }

    [LiveApiFact]
    public async Task Seed_deterministico_reproduz_mesma_base()
    {
        using var http = ApiClient.Create();

        async Task<JsonElement> FirstPageAfterSeedAsync(int count, int seed)
        {
            using var s = await http.PostAsync("/admin/seed", ApiClient.JsonBody(new { count, seed }));
            Assert.True(s.IsSuccessStatusCode);
            using var search = await http.GetAsync("/api/jobs/search?sort=newest&pageSize=3");
            return await ApiClient.ReadJson(search);
        }

        var a = await FirstPageAfterSeedAsync(300, 42);
        var b = await FirstPageAfterSeedAsync(300, 42);

        var idsA = a.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        var idsB = b.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Equal(idsA, idsB);
        Assert.Equal(a.GetProperty("total").GetInt64(), b.GetProperty("total").GetInt64());
    }

    [LiveApiFact]
    public async Task Reset_zera_base_e_contadores()
    {
        using var http = ApiClient.Create();
        using (var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob("Reset Test"))))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using (var reset = await http.PostAsync("/admin/reset", null))
            Assert.True(reset.IsSuccessStatusCode);

        // Contadores zerados (verificados antes da busca de confirmação, que gera um miss)
        var stats = await CacheStats(http);
        Assert.Equal(0, stats.GetProperty("details").GetProperty("hit").GetInt64());
        Assert.Equal(0, stats.GetProperty("search").GetProperty("miss").GetInt64());

        using (var search = await http.GetAsync("/api/jobs/search?pageSize=1"))
        {
            var body = await ApiClient.ReadJson(search);
            Assert.Equal(0, body.GetProperty("total").GetInt64());
        }
    }

    [LiveApiFact]
    public async Task CacheStats_exibe_configuracao_ativa()
    {
        using var http = ApiClient.Create();
        var stats = await CacheStats(http);
        var config = stats.GetProperty("config");
        Assert.True(config.GetProperty("detailsTtlSeconds").GetInt32() > 0);
        Assert.True(config.GetProperty("searchTtlSeconds").GetInt32() > 0);
    }
}
