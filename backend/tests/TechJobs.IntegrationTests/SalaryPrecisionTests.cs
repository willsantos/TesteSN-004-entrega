using System.Net;
using System.Text.Json;
using Xunit;

namespace TechJobs.IntegrationTests;

/// <summary>
/// Precisão salarial coerente entre escrita (POST/PUT), detalhe (GET) e índice (busca):
/// salários são normalizados para 2 casas no servidor (round half away from zero, igual
/// ao numeric(14,2) do PostgreSQL), o POST/PUT devolve o valor persistido (read-back na
/// mesma transação) e o índice usa scaled_float com escala 2 — centavos exatos nas
/// respostas e nos filtros inclusivos.
/// </summary>
public class SalaryPrecisionTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        using var http = ApiClient.Create();
        // Reset garante base limpa e contadores zerados para asserções determinísticas.
        using var reset = await http.PostAsync("/admin/reset", null);
        Assert.True(reset.IsSuccessStatusCode, "reset falhou — ENABLE_TEST_ENDPOINTS ativo?");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object Job(decimal? min, decimal? max) => new
    {
        title = "Desenvolvedor(a) Backend .NET Pleno",
        company = "Acme Tech",
        description = "Desenvolvimento de APIs e servicos com C#, PostgreSQL, Redis e testes automatizados. Trabalho colaborativo com revisao de codigo.",
        technologies = new[] { "C#", ".NET" },
        seniority = "pleno",
        workMode = "remoto",
        city = (string?)null,
        salaryMin = min,
        salaryMax = max,
        status = "aberta",
    };

    private static async Task DrainAsync()
    {
        using var http = ApiClient.Create();
        using var response = await http.PostAsync("/admin/indexing/drain", null);
        Assert.True(response.IsSuccessStatusCode);
    }

    private static async Task<JsonElement> Search(HttpClient http, string query)
    {
        using var response = await http.GetAsync($"/api/jobs/search{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiClient.ReadJson(response);
    }

    [LiveApiFact]
    public async Task POST_e_PUT_com_mais_de_duas_casas_representam_valor_oficial()
    {
        using var http = ApiClient.Create();
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(Job(6500.005m, 9000.005m)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await ApiClient.ReadJson(created);
        Assert.Equal(6500.01m, body.GetProperty("salaryMin").GetDecimal());
        Assert.Equal(9000.01m, body.GetProperty("salaryMax").GetDecimal());
        var id = body.GetProperty("id").GetString()!;

        // GET pelo id devolve EXATAMENTE os mesmos valores do POST (read-back na transação).
        using var get1 = await http.GetAsync($"/api/jobs/{id}");
        Assert.Equal(HttpStatusCode.OK, get1.StatusCode);
        var detailed = await ApiClient.ReadJson(get1);
        Assert.Equal(6500.01m, detailed.GetProperty("salaryMin").GetDecimal());
        Assert.Equal(9000.01m, detailed.GetProperty("salaryMax").GetDecimal());

        // PUT com mais de duas casas: resposta também já normalizada (caminho de UPDATE real).
        using var put = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(Job(7000.009m, 9500.009m)));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var putBody = await ApiClient.ReadJson(put);
        Assert.Equal(7000.01m, putBody.GetProperty("salaryMin").GetDecimal());
        Assert.Equal(9500.01m, putBody.GetProperty("salaryMax").GetDecimal());

        using var get2 = await http.GetAsync($"/api/jobs/{id}");
        var detailed2 = await ApiClient.ReadJson(get2);
        Assert.Equal(7000.01m, detailed2.GetProperty("salaryMin").GetDecimal());
        Assert.Equal(9500.01m, detailed2.GetProperty("salaryMax").GetDecimal());
    }

    [LiveApiFact]
    public async Task Salario_acima_do_limite_retorna_400()
    {
        using var http = ApiClient.Create();
        using var response = await http.PostAsync("/api/jobs", ApiClient.JsonBody(Job(1_000_000_000_000m, null)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ApiClient.ReadJson(response);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Contains("999999999999.99", body.GetProperty("detail").GetString());
    }

    [LiveApiFact]
    public async Task Filtros_inclusivos_nao_perdem_centavos()
    {
        // salaryMin=5000.005 e salaryMax=5000.005 → 5000.01 (normalizado, indexado em scaled_float).
        using var created = await ApiClient.Create().PostAsync("/api/jobs", ApiClient.JsonBody(Job(5000.005m, 5000.005m)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await DrainAsync();

        using var http = ApiClient.Create();
        // minSalary=X casa com salaryMax >= X (inclusivo): 5000.01 >= 5000.01 → encontra.
        var atBoundary = await Search(http, "?minSalary=5000.01");
        Assert.Equal(1, atBoundary.GetProperty("total").GetInt64());
        Assert.Equal(5000.01m, atBoundary.GetProperty("items")[0].GetProperty("salaryMin").GetDecimal());

        // Um centavo acima: 5000.01 >= 5000.02 é falso → não encontra.
        var above = await Search(http, "?minSalary=5000.02");
        Assert.Equal(0, above.GetProperty("total").GetInt64());

        // maxSalary=X casa com salaryMin <= X: 5000.01 <= 5000.01 → encontra; 5000.00 → não.
        var maxBoundary = await Search(http, "?maxSalary=5000.01");
        Assert.Equal(1, maxBoundary.GetProperty("total").GetInt64());
        var maxAbove = await Search(http, "?maxSalary=5000.00");
        Assert.Equal(0, maxAbove.GetProperty("total").GetInt64());
    }

    [LiveApiFact]
    public async Task PUT_idempotente_mantem_precisao()
    {
        using var http = ApiClient.Create();
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(Job(6500.005m, 9000.005m)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString()!;

        using var put1 = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(Job(6500.005m, 9000.005m)));
        Assert.Equal(HttpStatusCode.OK, put1.StatusCode);
        var first = await ApiClient.ReadJson(put1);
        Assert.Equal(6500.01m, first.GetProperty("salaryMin").GetDecimal());
        Assert.Equal(9000.01m, first.GetProperty("salaryMax").GetDecimal());

        await Task.Delay(1100); // resolução de clock visível caso updatedAt mudasse
        using var put2 = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(Job(6500.005m, 9000.005m)));
        Assert.Equal(HttpStatusCode.OK, put2.StatusCode);
        var second = await ApiClient.ReadJson(put2);

        // Mesmo payload reaplicado: resposta idêntica e updatedAt preservado (conteúdo
        // normalizado 6500.01 é igual ao persistido — caminho idempotente do PUT).
        Assert.Equal(first.GetProperty("salaryMin").GetDecimal(), second.GetProperty("salaryMin").GetDecimal());
        Assert.Equal(first.GetProperty("salaryMax").GetDecimal(), second.GetProperty("salaryMax").GetDecimal());
        Assert.Equal(first.GetProperty("publishedAt").GetString(), second.GetProperty("publishedAt").GetString());
        Assert.Equal(first.GetProperty("updatedAt").GetString(), second.GetProperty("updatedAt").GetString());
    }
}
