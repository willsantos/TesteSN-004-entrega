using System.Net;
using System.Text.Json;
using Xunit;

namespace TechJobs.IntegrationTests;

/// <summary>
/// Single-flight no cache: rajada fria gera um único miss (detalhe e busca, chaves
/// distintas contam separadas) e PUT com Redis fora deixa invalidação pendente, drenada
/// na primeira operação bem-sucedida após a recuperação.
/// </summary>
public class CacheSingleFlightTests : IAsyncLifetime
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
        company = "Single Flight Ltda",
        description = "Descrição suficientemente longa para passar na validação do domínio de vagas.",
        technologies = new[] { "C#", "Redis" },
        seniority = "pleno",
        workMode = "remoto",
        city = (string?)null,
        salaryMin = 5000,
        salaryMax = 7000,
        status = "aberta",
    };

    private static async Task<string> CreateJobAsync(HttpClient http, string title)
    {
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob(title)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ApiClient.ReadJson(created)).GetProperty("id").GetString()!;
    }

    private static async Task DrainIndexingAsync(HttpClient http)
    {
        using var drain = await http.PostAsync("/admin/indexing/drain", null);
        Assert.True(drain.IsSuccessStatusCode);
    }

    [LiveApiFact]
    public async Task Rajada_fria_no_detalhe_gera_um_unico_miss()
    {
        using var http = ApiClient.Create();
        var id = await CreateJobAsync(http, "Rajada Fria Detalhe");
        await DrainIndexingAsync(http);

        // 12 GETs concorrentes na mesma chave: um líder popula, os seguidores revalidam.
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var response = await http.GetAsync($"/api/jobs/{id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await ApiClient.ReadJson(response);
        }));

        var title = responses[0].GetProperty("title").GetString();
        Assert.Equal("Rajada Fria Detalhe", title);
        Assert.All(responses, r => Assert.Equal(title, r.GetProperty("title").GetString()));

        var stats = await CacheStats(http);
        Assert.Equal(1, stats.GetProperty("details").GetProperty("miss").GetInt64());
        var hits = stats.GetProperty("details").GetProperty("hit").GetInt64();
        Assert.True(hits >= 11, $"esperado details.hit >= 11, veio {hits}");
    }

    [LiveApiFact]
    public async Task Rajada_fria_na_busca_gera_um_unico_miss()
    {
        using var http = ApiClient.Create();
        using (var seed = await http.PostAsync("/admin/seed", ApiClient.JsonBody(new { count = 50, seed = 7 })))
            Assert.True(seed.IsSuccessStatusCode);
        await DrainIndexingAsync(http);

        static async Task Fire12Async(HttpClient http, string query)
        {
            await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            {
                using var response = await http.GetAsync($"/api/jobs/search{query}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await ApiClient.ReadJson(response);
            }));
        }

        // Mesma query 12× concorrentes: um único miss (um único acesso ao Elasticsearch).
        await Fire12Async(http, "?q=desenvolvedor&pageSize=5");
        var stats1 = await CacheStats(http);
        Assert.Equal(1, stats1.GetProperty("search").GetProperty("miss").GetInt64());
        var hits = stats1.GetProperty("search").GetProperty("hit").GetInt64();
        Assert.True(hits >= 11, $"esperado search.hit >= 11, veio {hits}");

        // Outra query (chave canônica distinta): mais um miss no acumulado.
        await Fire12Async(http, "?q=kubernetes&pageSize=5");
        var stats2 = await CacheStats(http);
        Assert.Equal(2, stats2.GetProperty("search").GetProperty("miss").GetInt64());
    }

    [LiveApiFact]
    public async Task PUT_com_redis_fora_invalida_ao_recuperar()
    {
        using var http = ApiClient.Create();
        var id = await CreateJobAsync(http, "Pendente v1");

        // Popula o cache de detalhe (v1).
        using (var get = await http.GetAsync($"/api/jobs/{id}"))
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        // Redis fora: PUT persiste v2 no PostgreSQL; o DEL falha e vira delete pendente.
        await SetFault(http, "redis", true);
        using (var put = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(ValidJob("Pendente v2"))))
            Assert.True(put.IsSuccessStatusCode);
        var statsFault = await CacheStats(http);
        Assert.True(statsFault.GetProperty("details").GetProperty("error").GetInt64() >= 1);

        // Redis volta: o GET drena o delete pendente; a 1ª leitura ainda pode trazer v1.
        await SetFault(http, "redis", false);
        using (var get1 = await http.GetAsync($"/api/jobs/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, get1.StatusCode);
            var first = await ApiClient.ReadJson(get1);
            Assert.Equal(id, first.GetProperty("id").GetString());
        }

        // A chave antiga não sobrevive: 2ª leitura obrigatoriamente reflete v2.
        using (var get2 = await http.GetAsync($"/api/jobs/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, get2.StatusCode);
            var body = await ApiClient.ReadJson(get2);
            Assert.Equal("Pendente v2", body.GetProperty("title").GetString());
        }
    }
}
