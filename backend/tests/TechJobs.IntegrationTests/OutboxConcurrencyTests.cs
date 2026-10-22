using System.Net;
using System.Text.Json;
using Xunit;

namespace TechJobs.IntegrationTests;

/// <summary>
/// Regressão da outbox de indexação (eventos imutáveis, um por escrita):
/// PUT concorrente ao consumo, reindex concorrente com escrita e drain/worker
/// simultâneos não podem perder nem duplicar atualizações.
/// </summary>
public class OutboxConcurrencyTests : IAsyncLifetime
{
    // Termos de busca únicos (só existem nos títulos das vagas-alvo destes testes).
    private const string MarcadorPut = "qxzoutboxput";
    private const string MarcadorReindex = "qxzreindexput";

    public async Task InitializeAsync()
    {
        using var http = ApiClient.Create();
        using var reset = await http.PostAsync("/admin/reset", null);
        Assert.True(reset.IsSuccessStatusCode);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task DrainAsync()
    {
        using var http = ApiClient.Create();
        using var response = await http.PostAsync("/admin/indexing/drain", null);
        Assert.True(response.IsSuccessStatusCode);
    }

    private static object ValidJob(string title) => new
    {
        title,
        company = "Outbox Ltda",
        description = "Descrição suficientemente longa para passar na validação do domínio de vagas.",
        technologies = new[] { "C#", "PostgreSQL" },
        seniority = "pleno",
        workMode = "remoto",
        city = (string?)null,
        salaryMin = 5000,
        salaryMax = 7000,
        status = "aberta",
    };

    [LiveApiFact]
    public async Task PUT_concorrente_durante_consumo_do_outbox_nao_perde_versao()
    {
        using var http = ApiClient.Create();

        // 1. Backlog de ~600 vagas: o worker consome em lotes de 500 → várias batidas de consumo.
        //    A vaga-alvo é a PRIMEIRA criada (entra no primeiro lote consumido).
        string primeiroId;
        {
            using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob("Backlog Outbox 000")));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            primeiroId = (await ApiClient.ReadJson(created)).GetProperty("id").GetString()!;
        }
        for (var i = 1; i < 600; i++)
        {
            using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob($"Backlog Outbox {i:000}")));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // 2. PUTs na vaga-alvo enquanto o worker consome o backlog, alternando versões do título.
        var prazo = DateTime.UtcNow.AddSeconds(5);
        var ultimoTitulo = "";
        for (var i = 0; i < 200 && DateTime.UtcNow < prazo; i++)
        {
            ultimoTitulo = $"Tech Lead — versão {(i % 2 == 0 ? "A" : "B")} {MarcadorPut}";
            using var put = await http.PutAsync($"/api/jobs/{primeiroId}", ApiClient.JsonBody(ValidJob(ultimoTitulo)));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        // 3. Drain duplo: consome também eventos enfileirados após o primeiro peek.
        await DrainAsync();
        await Task.Delay(500);
        await DrainAsync();

        // 4. A versão do ÚLTIMO PUT tem que estar visível (o consumo não pode apagá-la).
        using var search = await http.GetAsync($"/api/jobs/search?q={MarcadorPut}&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var body = await ApiClient.ReadJson(search);
        Assert.Equal(1, body.GetProperty("total").GetInt64());
        var item = body.GetProperty("items")[0];
        Assert.Equal(primeiroId, item.GetProperty("id").GetString());
        Assert.Equal(ultimoTitulo, item.GetProperty("title").GetString());
    }

    [LiveApiFact]
    public async Task Reindex_concorrente_com_escrita_nao_perde_evento()
    {
        using var http = ApiClient.Create();
        using (var seed = await http.PostAsync("/admin/seed", ApiClient.JsonBody(new { count = 3000, seed = 7 })))
            Assert.True(seed.IsSuccessStatusCode);
        await DrainAsync();

        // Vaga conhecida que receberá o PUT durante o reindex.
        string alvoId;
        using (var search = await http.GetAsync("/api/jobs/search?pageSize=1"))
        {
            var body = await ApiClient.ReadJson(search);
            alvoId = body.GetProperty("items")[0].GetProperty("id").GetString()!;
        }

        // Reindex EM ANDAMENTO + PUT concorrente com título único.
        var reindexTask = http.PostAsync("/admin/indexing/reindex", null);
        var novoTitulo = $"Marcador Reindex Concorrente {MarcadorReindex}";
        using (var put = await http.PutAsync($"/api/jobs/{alvoId}", ApiClient.JsonBody(ValidJob(novoTitulo))))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using (var reindex = await reindexTask)
            Assert.True(reindex.IsSuccessStatusCode);

        // O evento do PUT é posterior ao watermark do snapshot: sobrevive e é aplicado.
        await DrainAsync();
        await Task.Delay(2000); // cobre o tick de 1s do worker

        using var final = await http.GetAsync($"/api/jobs/search?q={MarcadorReindex}&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        var resultado = await ApiClient.ReadJson(final);
        Assert.Equal(1, resultado.GetProperty("total").GetInt64());
        Assert.Equal(alvoId, resultado.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(novoTitulo, resultado.GetProperty("items")[0].GetProperty("title").GetString());
    }

    [LiveApiFact]
    public async Task Drain_e_worker_simultaneos_nao_duplicam_nem_perdem()
    {
        using var http = ApiClient.Create();

        var criadas = 0;
        for (var i = 0; i < 40; i++)
        {
            using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob($"Simultaneo Outbox {i:000}")));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            criadas++;
        }

        // 3 drains concorrentes enquanto novas escritas chegam (worker segue consumindo).
        var drains = new[]
        {
            http.PostAsync("/admin/indexing/drain", null),
            http.PostAsync("/admin/indexing/drain", null),
            http.PostAsync("/admin/indexing/drain", null),
        };
        for (var i = 0; i < 30; i++)
        {
            using var created = await http.PostAsync("/api/jobs", ApiClient.JsonBody(ValidJob($"Simultaneo Outbox Extra {i:000}")));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            criadas++;
        }
        foreach (var response in await Task.WhenAll(drains))
        {
            Assert.True(response.IsSuccessStatusCode);
            response.Dispose();
        }

        await DrainAsync();

        // total == POSTs com 201 e nenhum documento duplicado (varre todas as páginas).
        var ids = new List<string>();
        long total = 0;
        for (var page = 1; ; page++)
        {
            using var response = await http.GetAsync($"/api/jobs/search?page={page}&pageSize=50");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ApiClient.ReadJson(response);
            total = body.GetProperty("total").GetInt64();
            var items = body.GetProperty("items");
            ids.AddRange(items.EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
            if (items.GetArrayLength() == 0 || ids.Count >= total) break;
        }
        Assert.Equal(criadas, total);
        Assert.Equal(criadas, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
