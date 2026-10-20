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
/// </summary>
public sealed class LiveApiFactAttribute : FactAttribute
{
    public LiveApiFactAttribute()
    {
        var baseUrl = Environment.GetEnvironmentVariable("TECHJOBS_API") ?? "http://localhost:8080";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = http.GetAsync(baseUrl + "/health").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                Skip = $"API respondeu {(int)response.StatusCode} em {baseUrl}; suba a stack com docker compose up.";
        }
        catch (Exception)
        {
            Skip = $"API indisponível em {baseUrl} (docker compose up). Testes de integração pulados.";
        }
    }
}

public static class ApiClient
{
    public static readonly string BaseUrl = Environment.GetEnvironmentVariable("TECHJOBS_API") ?? "http://localhost:8080";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HttpClient Create()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
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
