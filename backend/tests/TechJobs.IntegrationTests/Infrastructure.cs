using Xunit;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace TechJobs.IntegrationTests;

/// <summary>
/// Testes black-box contra a stack completa (docker compose up). A base URL vem de
/// TECHJOBS_API (padrão http://localhost:8080). Sem a API no ar, os testes são
/// pulados (Skip) — não falham — para não atrapalhar `dotnet test` unit-only.
/// Com TECHJOBS_REQUIRE_API=true|1 (modo estrito, usado no CI), NUNCA pula: se a
/// API cair entre a descoberta e a execução, o teste roda e falha na primeira
/// chamada HTTP (conexão recusada) — suíte vermelha em vez de falso verde.
/// </summary>
public static class ApiProbe
{
    /// <summary>Modo estrito: a API é obrigatória (CI) — Skip não é opção.</summary>
    public static bool StrictMode =>
        Environment.GetEnvironmentVariable("TECHJOBS_REQUIRE_API") is "true" or "1";

    /// <summary>Probe GET {baseUrl}/health; devolve o motivo de Skip, ou null se a API está no ar.</summary>
    public static string? SkipReason(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = http.GetAsync(baseUrl + "/health").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return $"API respondeu {(int)response.StatusCode} em {baseUrl}; suba a stack com docker compose up.";
            return null;
        }
        catch (Exception)
        {
            return $"API indisponível em {baseUrl} (docker compose up). Testes de integração pulados.";
        }
    }

    /// <summary>
    /// Decide o Skip no construtor do atributo: em modo estrito deixa Skip nulo
    /// (o teste executa e falha naturalmente se a API não estiver no ar).
    /// </summary>
    public static void ApplySkip(FactAttribute attribute, string baseUrl)
    {
        if (StrictMode)
            return;
        attribute.Skip = SkipReason(baseUrl);
    }
}

/// <summary>API principal (TECHJOBS_API, porta 8080).</summary>
public sealed class LiveApiFactAttribute : FactAttribute
{
    public LiveApiFactAttribute() =>
        ApiProbe.ApplySkip(this, ApiClient.BaseUrl);
}

/// <summary>Instância sem ENABLE_TEST_ENDPOINTS (TECHJOBS_API_NOADMIN, porta 8085).</summary>
public sealed class NoAdminApiFactAttribute : FactAttribute
{
    public NoAdminApiFactAttribute() =>
        ApiProbe.ApplySkip(this, ApiClient.NoAdminBaseUrl);
}

/// <summary>Instância com TTL de cache de 2s (TECHJOBS_API_SHORTTTL, porta 8086).</summary>
public sealed class ShortTtlApiFactAttribute : FactAttribute
{
    public ShortTtlApiFactAttribute() =>
        ApiProbe.ApplySkip(this, ApiClient.ShortTtlBaseUrl);
}

public static class ApiClient
{
    public static readonly string BaseUrl = Environment.GetEnvironmentVariable("TECHJOBS_API") ?? "http://localhost:8080";
    public static readonly string NoAdminBaseUrl = Environment.GetEnvironmentVariable("TECHJOBS_API_NOADMIN") ?? "http://localhost:8085";
    public static readonly string ShortTtlBaseUrl = Environment.GetEnvironmentVariable("TECHJOBS_API_SHORTTTL") ?? "http://localhost:8086";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HttpClient Create() => For(BaseUrl);

    /// <summary>Cliente apontando para a instância auxiliar sem endpoints /admin.</summary>
    public static HttpClient ForNoAdmin() => For(NoAdminBaseUrl);

    /// <summary>Cliente apontando para a instância auxiliar com TTL curto (2s).</summary>
    public static HttpClient ForShortTtl() => For(ShortTtlBaseUrl);

    public static HttpClient For(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj, Json), Encoding.UTF8, "application/json");

    public static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content).RootElement.Clone();
    }
}
