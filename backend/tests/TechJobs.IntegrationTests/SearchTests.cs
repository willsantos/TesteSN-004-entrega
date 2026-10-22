using System.Net;
using System.Text.Json;
using Xunit;

namespace TechJobs.IntegrationTests;

/// <summary>Busca pública: filtros, semântica salarial, ordenação, paginação e estatísticas.</summary>
public class SearchTests : IAsyncLifetime
{
    private readonly List<string> _createdIds = [];

    public async Task InitializeAsync()
    {
        using var http = ApiClient.Create();
        // Reset garante base limpa e contadores zerados para asserções determinísticas.
        using var reset = await http.PostAsync("/admin/reset", null);
        Assert.True(reset.IsSuccessStatusCode, "reset falhou — ENABLE_TEST_ENDPOINTS ativo?");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> CreateJobAsync(object payload)
    {
        using var http = ApiClient.Create();
        using var response = await http.PostAsync("/api/jobs", ApiClient.JsonBody(payload));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ApiClient.ReadJson(response)).GetProperty("id").GetString()!;
        _createdIds.Add(id);
        return id;
    }

    private static object Job(
        string title, string company, string description, string[] techs,
        string seniority, string workMode, string? city = null,
        decimal? min = null, decimal? max = null, string status = "aberta") => new
    {
        title, company, description, technologies = techs, seniority, workMode,
        city, salaryMin = min, salaryMax = max, status,
    };

    private static async Task<JsonElement> Search(HttpClient http, string query)
    {
        using var response = await http.GetAsync($"/api/jobs/search{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiClient.ReadJson(response);
    }

    private static async Task DrainAsync()
    {
        using var http = ApiClient.Create();
        using var response = await http.PostAsync("/admin/indexing/drain", null);
        Assert.True(response.IsSuccessStatusCode);
    }

    [LiveApiFact]
    public async Task Busca_respeita_filtros_exatos_e_TODAS_as_tecnologias()
    {
        var target = await CreateJobAsync(Job(
            "Desenvolvedor Backend Go Sênior", "Filtro Ltda",
            "Vaga para construir microsserviços em Go com RabbitMQ e MongoDB no cloude.",
            ["Go", "RabbitMQ", "MongoDB"], "senior", "remoto", min: 15000, max: 18000));
        await CreateJobAsync(Job(
            "Desenvolvedor Backend Go Pleno", "Filtro Ltda",
            "Vaga para construir microsserviços em Go com MongoDB apenas.",
            ["Go", "MongoDB"], "pleno", "presencial", "Recife", min: 8000, max: 9000));
        await DrainAsync();

        using var http = ApiClient.Create();
        // TODAS: Go + RabbitMQ → só a sênior
        var both = await Search(http, "?technologies=go,rabbitmq");
        Assert.Equal(1, both.GetProperty("total").GetInt64());
        Assert.Equal(target, both.GetProperty("items")[0].GetProperty("id").GetString());

        // tecnologia case-insensitive
        var caseInsensitive = await Search(http, "?technologies=GO,RABBITMQ");
        Assert.Equal(1, caseInsensitive.GetProperty("total").GetInt64());

        // seniority exato
        var senior = await Search(http, "?seniority=senior&technologies=go");
        Assert.Equal(1, senior.GetProperty("total").GetInt64());

        // workMode exato
        var presencial = await Search(http, "?workMode=presencial&technologies=go");
        Assert.Equal(1, presencial.GetProperty("total").GetInt64());
        Assert.Equal("pleno", presencial.GetProperty("items")[0].GetProperty("seniority").GetString());
    }

    [LiveApiFact]
    public async Task Filtro_de_tecnologia_inexistente_zera_resultados()
    {
        await CreateJobAsync(Job("Desenvolvedor Full-Stack React", "Tech Mix",
            "Frontend com React e backend com Node.js em produto de alto tráfego.",
            ["React", "Node.js"], "pleno", "remoto"));
        await DrainAsync();
        using var http = ApiClient.Create();
        var none = await Search(http, "?technologies=cobol");
        Assert.Equal(0, none.GetProperty("total").GetInt64());
        Assert.Empty(none.GetProperty("items").EnumerateArray());
    }

    [LiveApiFact]
    public async Task Semantica_salarial_intersecao_e_exclusao_de_sem_salario()
    {
        // [10000, 12000] casa com minSalary=11000 (salaryMax >= 11000) e maxSalary=11000 (salaryMin <= 11000)
        await CreateJobAsync(Job("Engenheiro de Dados Sênior", "Dados & Cia",
            "Pipelines de dados com Python, Airflow e Spark em larga escala diária.",
            ["Python", "Spark"], "senior", "hibrido", "Curitiba", 10000, 12000));
        // Sem salário → excluída de qualquer filtro salarial
        await CreateJobAsync(Job("Engenheiro de Dados Pleno", "Dados & Cia",
            "Pipelines de dados com Python e Airflow em escala média.",
            ["Python"], "pleno", "remoto"));
        await DrainAsync();

        using var http = ApiClient.Create();
        var minOnly = await Search(http, "?minSalary=11000&technologies=python");
        Assert.Equal(1, minOnly.GetProperty("total").GetInt64());
        Assert.Equal(12000, minOnly.GetProperty("items")[0].GetProperty("salaryMax").GetDecimal());

        var maxOnly = await Search(http, "?maxSalary=11000&technologies=python");
        Assert.Equal(1, maxOnly.GetProperty("total").GetInt64());
        Assert.Equal(10000, maxOnly.GetProperty("items")[0].GetProperty("salaryMin").GetDecimal());

        // Interseção inclusiva: [10999, 11001] ∩ [10000, 12000] = não vazio
        var both = await Search(http, "?minSalary=10999&maxSalary=11001&technologies=python");
        Assert.Equal(1, both.GetProperty("total").GetInt64());

        // Sem salário não aparece mesmo com minSalary=0
        var anyMin = await Search(http, "?minSalary=0&technologies=python");
        Assert.Equal(1, anyMin.GetProperty("total").GetInt64());

        // Fora da faixa → zero
        var tooHigh = await Search(http, "?minSalary=12000.01&technologies=python");
        Assert.Equal(0, tooHigh.GetProperty("total").GetInt64());
    }

    [LiveApiFact]
    public async Task Ordenacoes_newest_salary_desc_e_relevance()
    {
        var older = await CreateJobAsync(Job("Cientista de Dados Júnior", "Alpha",
            "Modelos preditivos com Python e scikit-learn para risco de crédito.",
            ["Python"], "junior", "remoto", min: 4000, max: 5000));
        await Task.Delay(50);
        var newer = await CreateJobAsync(Job("Cientista de Dados Sênior", "Alpha",
            "Modelos preditivos com Python, Spark e MLOps para risco de crédito.",
            ["Python", "Spark"], "senior", "remoto", min: 17000, max: 25000));
        await DrainAsync();

        using var http = ApiClient.Create();
        var newest = await Search(http, "?sort=newest&pageSize=2");
        var firstNewest = newest.GetProperty("items")[0].GetProperty("id").GetString();
        Assert.Equal(newer, firstNewest);

        var salary = await Search(http, "?sort=salary_desc&pageSize=2");
        var firstSalary = salary.GetProperty("items")[0].GetProperty("id").GetString();
        Assert.Equal(newer, firstSalary); // 25000 > 5000

        // relevance sem q degrada para newest (documentado)
        var degraded = await Search(http, "?pageSize=2");
        Assert.Equal(newer, degraded.GetProperty("items")[0].GetProperty("id").GetString());
        _ = older;
    }

    [LiveApiFact]
    public async Task Relevance_pesa_titulo_mais_que_descricao()
    {
        // "kubernetes" só na descrição vs. só no título
        await CreateJobAsync(Job("Analista de Operações", "Ops Soft",
            "Rotinas de infraestrutura com kubernetes e monitoramento de clusters.",
            ["Docker"], "pleno", "remoto"));
        await CreateJobAsync(Job("Engenheiro Kubernetes Sênior", "Ops Soft",
            "Administração de clusters e rotinas de infraestrutura com monitoramento.",
            ["Docker", "Kubernetes"], "senior", "remoto"));
        await DrainAsync();

        using var http = ApiClient.Create();
        var relevance = await Search(http, "?q=kubernetes&sort=relevance");
        Assert.Equal(2, relevance.GetProperty("total").GetInt64());
        Assert.Equal("Engenheiro Kubernetes Sênior",
            relevance.GetProperty("items")[0].GetProperty("title").GetString());
    }

    [LiveApiFact]
    public async Task Busca_textual_e_case_insensitive_e_semantica_E()
    {
        await CreateJobAsync(Job("Desenvolvedor React e Docker", "Websys",
            "Aplicação web com React no front, empacotada com Docker e deploy contínuo.",
            ["React", "Docker"], "pleno", "remoto"));
        await CreateJobAsync(Job("Desenvolvedor React", "Websys",
            "Somente frontend com React e design system próprio, foco em acessibilidade.",
            ["React"], "junior", "remoto"));
        await DrainAsync();

        using var http = ApiClient.Create();
        // E: react + docker → só a vaga que contém os dois termos no documento
        var bothTerms = await Search(http, "?q=react docker");
        Assert.Equal(1, bothTerms.GetProperty("total").GetInt64());
        Assert.Equal("Desenvolvedor React e Docker", bothTerms.GetProperty("items")[0].GetProperty("title").GetString());

        var upper = await Search(http, "?q=REACT");
        Assert.Equal(2, upper.GetProperty("total").GetInt64());
    }

    [LiveApiFact]
    public async Task Paginacao_total_e_pagina_fora_do_fim()
    {
        for (var i = 0; i < 12; i++)
            await CreateJobAsync(Job($"Desenvolvedor(a) Python {i:00} Pleno", "Paginação Ltda",
                "Automação de processos com Python, pandas e APIs internas de dados.",
                ["Python"], "pleno", "remoto"));
        await DrainAsync();

        using var http = ApiClient.Create();
        var page1 = await Search(http, "?pageSize=10");
        Assert.Equal(10, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(12, page1.GetProperty("total").GetInt64());
        Assert.Equal(1, page1.GetProperty("page").GetInt32());
        Assert.Equal(10, page1.GetProperty("pageSize").GetInt32());

        var page2 = await Search(http, "?page=2&pageSize=10");
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(12, page2.GetProperty("total").GetInt64());

        var beyond = await Search(http, "?page=99&pageSize=10");
        Assert.Equal(0, beyond.GetProperty("items").GetArrayLength());
        Assert.Equal(12, beyond.GetProperty("total").GetInt64());
    }

    [LiveApiFact]
    public async Task Vaga_encerrada_sai_da_busca_mas_continua_no_detalhe()
    {
        var id = await CreateJobAsync(Job("Dev PL SQL Sênior", "Legado SA",
            "Manutenção de sistemas legados com PL SQL eOracle, migração gradual.",
            ["PL SQL"], "senior", "presencial", "Salvador", 9000, 11000));
        await DrainAsync();
        using var http = ApiClient.Create();

        var before = await Search(http, "?q=legado");
        Assert.Equal(1, before.GetProperty("total").GetInt64());

        var closed = Job("Dev PL SQL Sênior", "Legado SA",
            "Manutenção de sistemas legados com PL SQL eOracle, migração gradual.",
            ["PL SQL"], "senior", "presencial", "Salvador", 9000, 11000, "encerrada");
        using var put = await http.PutAsync($"/api/jobs/{id}", ApiClient.JsonBody(closed));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await DrainAsync();

        var after = await Search(http, "?q=legado");
        Assert.Equal(0, after.GetProperty("total").GetInt64());

        using var detail = await http.GetAsync($"/api/jobs/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("encerrada", (await ApiClient.ReadJson(detail)).GetProperty("status").GetString());
    }

    [LiveApiFact]
    public async Task Stats_somam_totalOpenJobs_e_topTechnologies_minimo_10()
    {
        using var seed = await ApiClient.Create().PostAsync("/admin/seed",
            ApiClient.JsonBody(new { count = 800, seed = 42 }));
        Assert.True(seed.IsSuccessStatusCode);

        using var http = ApiClient.Create();
        using var response = await http.GetAsync("/api/jobs/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stats = await ApiClient.ReadJson(response);

        var total = stats.GetProperty("totalOpenJobs").GetInt64();
        Assert.True(total > 0);

        var bySeniority = stats.GetProperty("bySeniority");
        Assert.Equal(total,
            bySeniority.GetProperty("junior").GetInt64()
            + bySeniority.GetProperty("pleno").GetInt64()
            + bySeniority.GetProperty("senior").GetInt64());

        var byWorkMode = stats.GetProperty("byWorkMode");
        Assert.Equal(total,
            byWorkMode.GetProperty("remoto").GetInt64()
            + byWorkMode.GetProperty("hibrido").GetInt64()
            + byWorkMode.GetProperty("presencial").GetInt64());

        var top = stats.GetProperty("topTechnologies");
        Assert.True(top.GetArrayLength() >= 10, $"esperado >= 10 tecnologias, veio {top.GetArrayLength()}");
        // contagens decrescentes
        var counts = top.EnumerateArray().Select(t => t.GetProperty("count").GetInt64()).ToList();
        Assert.Equal(counts.OrderByDescending(c => c), counts);
        // soma pode exceder totalOpenJobs (vaga contribui para várias tecnologias)
        Assert.True(counts.Sum() >= total);
    }

    [LiveApiFact]
    public async Task Busca_com_parametros_invalidos_retorna_400()
    {
        using var http = ApiClient.Create();
        foreach (var query in new[] { "?page=0", "?pageSize=51", "?pageSize=0", "?minSalary=-1", "?seniority=x", "?workMode=x", "?sort=x" })
        {
            using var response = await http.GetAsync($"/api/jobs/search{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [LiveApiFact]
    public async Task Busca_E_distribuida_entre_titulo_e_descricao()
    {
        // A: "kubernetes" só no título; "observabilidade" só na descrição
        var a = await CreateJobAsync(Job(
            "Engenheiro Kubernetes Sênior", "Cloud Dist",
            "Plataforma de contêineres com foco em observabilidade de serviços distribuídos em produção.",
            ["Docker"], "senior", "remoto"));
        // B: "kubernetes" no título, sem "observabilidade" em lugar nenhum
        await CreateJobAsync(Job(
            "Administrador Kubernetes Pleno", "Cloud Dist",
            "Operação de clusters gerenciados e automação de deploys de contêineres em escala.",
            ["Docker"], "pleno", "remoto"));
        // C: "observabilidade" na descrição, sem "kubernetes" em lugar nenhum
        await CreateJobAsync(Job(
            "Analista de Plataforma Sênior", "Cloud Dist",
            "Manutenção de ferramentas de observabilidade para times de produto em crescimento.",
            ["Grafana"], "senior", "remoto"));
        await DrainAsync();

        using var http = ApiClient.Create();
        // E distribuída: um termo no título e outro na descrição → casa somente a vaga A
        var both = await Search(http, "?q=kubernetes observabilidade");
        Assert.Equal(1, both.GetProperty("total").GetInt64());
        Assert.Equal(a, both.GetProperty("items")[0].GetProperty("id").GetString());

        // Termo isolado: A e B (título) aparecem, C fica de fora
        var kube = await Search(http, "?q=kubernetes");
        Assert.Equal(2, kube.GetProperty("total").GetInt64());
        var ids = kube.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Contains(a, ids);
    }

    [LiveApiFact]
    public async Task Busca_stemming_e_acentucao_tem_regressao_coberta()
    {
        // Descrição com "programação" (stemming de -ção) e "código" (folding de acento)
        var d = await CreateJobAsync(Job(
            "Desenvolvedor Backend Pleno", "Code Sa",
            "Manutenção de APIs com código limpo, testes automatizados e rotinas de programação em C#.",
            ["C#"], "pleno", "hibrido"));
        await DrainAsync();

        using var http = ApiClient.Create();
        // Stemming PT (e maiúsculas): flexão de gênero "DESENVOLVEDORA" casa com "Desenvolvedor"
        var stemming = await Search(http, "?q=DESENVOLVEDORA");
        Assert.Equal(1, stemming.GetProperty("total").GetInt64());
        Assert.Equal(d, stemming.GetProperty("items")[0].GetProperty("id").GetString());

        // Folding de acento: "codigo" (sem acento) casa com "código" da descrição
        var folding = await Search(http, "?q=codigo");
        Assert.Equal(1, folding.GetProperty("total").GetInt64());
        Assert.Equal(d, folding.GetProperty("items")[0].GetProperty("id").GetString());

        // "programação" (com acento, URL-encodada) casa com a descrição
        var acento = await Search(http, $"?q={Uri.EscapeDataString("programação")}");
        Assert.Equal(1, acento.GetProperty("total").GetInt64());
        Assert.Equal(d, acento.GetProperty("items")[0].GetProperty("id").GetString());
    }
}
