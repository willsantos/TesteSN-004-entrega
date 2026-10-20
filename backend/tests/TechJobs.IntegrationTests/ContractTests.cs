using System.Net;
using System.Text.Json;
using Xunit;

namespace TechJobs.IntegrationTests;

/// <summary>Contrato de API: rotas, códigos HTTP, shapes de JSON e Problem Details.</summary>
public class ContractTests
{
    private static readonly object ValidJob = new
    {
        title = "Desenvolvedor(a) Backend .NET Pleno",
        company = "Acme Tech",
        description = "Desenvolvimento de APIs e servicos com C#, PostgreSQL, Redis e testes automatizados. Trabalho colaborativo com revisao de codigo.",
        technologies = new[] { "C#", ".NET", "Redis", "PostgreSQL" },
        seniority = "pleno",
        workMode = "remoto",
        city = (string?)null,
        salaryMin = 6500,
        salaryMax = 8500,
        status = "aberta",
    };

    [LiveApiFact]
    public async Task Health_responde_200()
    {
        using var http = ApiClient.Create();
        using var response = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [LiveApiFact]
    public async Task POST_cria_vaga_com_201_location_e_campos_do_servidor()
    {
        using var http = ApiClient.Create();
        using var response = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.True(response.Headers.Location is not null, "Location obrigatório");
        Assert.True(response.Headers.Location!.ToString().StartsWith("/api/jobs/"),
            $"Location inesperado: {response.Headers.Location}");

        var body = await ApiClient.ReadJson(response);
        var id = Assert.IsType<Guid>(body.GetProperty("id").GetGuid());
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("pleno", body.GetProperty("seniority").GetString());
        Assert.Equal("remoto", body.GetProperty("workMode").GetString());
        Assert.Equal("aberta", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("publishedAt").GetDateTime() > DateTime.UtcNow.AddDays(-1));
        Assert.True(body.GetProperty("updatedAt").GetDateTime() > DateTime.UtcNow.AddDays(-1));
        Assert.Equal(6500, body.GetProperty("salaryMin").GetDecimal());
    }

    [LiveApiFact]
    public async Task GET_por_id_retorna_vaga_completa()
    {
        using var http = ApiClient.Create();
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob));
        var id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString();

        using var response = await http.GetAsync($"/api/jobs/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiClient.ReadJson(response);
        Assert.Equal(id, body.GetProperty("id").GetString());
        Assert.NotNull(body.GetProperty("description").GetString());
        Assert.NotNull(body.GetProperty("technologies"));
    }

    [LiveApiFact]
    public async Task GET_inexistente_retorna_404_problem_details()
    {
        using var http = ApiClient.Create();
        using var response = await http.GetAsync($"/api/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ApiClient.ReadJson(response);
        Assert.Equal(404, body.GetProperty("status").GetInt32());
        Assert.NotNull(body.GetProperty("type").GetString());
        Assert.NotNull(body.GetProperty("title").GetString());
        Assert.NotNull(body.GetProperty("detail").GetString());
    }

    [LiveApiFact]
    public async Task POST_invalido_retorna_400_problem_details_com_detalhe_da_faixa_salarial()
    {
        var payload = new
        {
            title = "Dev Backend .NET Pleno",
            company = "Acme Tech",
            description = "Desenvolvimento de APIs e servicos com C# e Redis.",
            technologies = new[] { "C#" },
            seniority = "pleno",
            workMode = "remoto",
            salaryMin = 6500,
            salaryMax = 5000,
        };
        using var http = ApiClient.Create();
        using var response = await http.PostAsync("/api/jobs", ApiClient.JsonBody(payload));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ApiClient.ReadJson(response);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Contains("salaryMax (5000) deve ser maior ou igual a salaryMin (6500)", body.GetProperty("detail").GetString());
    }

    [LiveApiFact]
    public async Task PUT_atualiza_e_PUT_inexistente_da_404()
    {
        using var http = ApiClient.Create();
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob));
        var id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString();

        var updated = new
        {
            title = "Desenvolvedor(a) Backend .NET Sênior",
            company = "Acme Tech",
            description = "Desenvolvimento de APIs e servicos com C#, PostgreSQL, Redis e testes automatizados. Trabalho colaborativo com revisao de codigo.",
            technologies = new[] { "C#", ".NET", "Redis" },
            seniority = "senior",
            workMode = "hibrido",
            city = "São Paulo",
            salaryMin = 12000,
            salaryMax = 16000,
            status = "aberta",
        };
        using var put = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(updated));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await ApiClient.ReadJson(put);
        Assert.Equal("senior", body.GetProperty("seniority").GetString());
        Assert.Equal("São Paulo", body.GetProperty("city").GetString());

        using var missing = await http.PutAsync($"/api/jobs/{Guid.NewGuid()}", ApiClient.JsonBody(updated));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [LiveApiFact]
    public async Task PUT_e_idempotente_mesmo_payload_nao_muda_updatedAt()
    {
        using var http = ApiClient.Create();
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob));
        var id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString();

        using var put1 = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(ValidJob));
        var updatedAt1 = (await ApiClient.ReadJson(put1)).GetProperty("updatedAt").GetString();
        await Task.Delay(1100); // resolução de clock visível caso updatedAt mudasse
        using var put2 = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(ValidJob));
        var updatedAt2 = (await ApiClient.ReadJson(put2)).GetProperty("updatedAt").GetString();

        Assert.Equal(updatedAt1, updatedAt2);
    }

    [LiveApiFact]
    public async Task PUT_invalido_retorna_400()
    {
        using var http = ApiClient.Create();
        using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob));
        var id = (await ApiClient.ReadJson(created)).GetProperty("id").GetString();

        var invalid = new
        {
            title = "Dev",
            company = "Acme Tech",
            description = "curta",
            technologies = new[] { "C#" },
            seniority = "guru",
            workMode = "remoto",
        };
        using var put = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(invalid));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }
}
