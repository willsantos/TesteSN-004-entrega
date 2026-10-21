using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Search;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Slices.JobStats;

public static class JobStatsEndpoint
{
    public static void MapJobStatsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs/stats", async (JobStatsRunner runner) =>
            Results.Json(await runner.StatsAsync()));
    }
}

/// <summary>
/// Estatísticas derivadas de agregações `terms` do índice `techjobs-v1`. A operação
/// checa a falha simulada primeiro e trata resposta inválida como indisponibilidade —
/// a leitura pública devolve 503.
/// </summary>
public sealed class JobStatsRunner
{
    private readonly ElasticsearchClient _client;
    private readonly FaultState _faults;

    public JobStatsRunner(ElasticsearchClient client, FaultState faults)
    {
        _client = client;
        _faults = faults;
    }

    private void EnsureAvailable()
    {
        if (_faults.ElasticDown)
            throw new SearchUnavailableException("A busca está temporariamente indisponível (Elasticsearch indisponível).");
    }

    public async Task<JobsStats> StatsAsync(CancellationToken ct = default)
    {
        EnsureAvailable();

        var response = await _client.SearchAsync<JobDoc>(s => s
            .Index(ElasticIndex.IndexName)
            .Size(0)
            .TrackTotalHits(new TrackHits(true))
            .Query(q => q.Term(t => t.Field("status").Value("aberta")))
            .Aggregations(aggs => aggs
                .Add("bySeniority", a => a.Terms(t => t.Field("seniority").Size(10)))
                .Add("byWorkMode", a => a.Terms(t => t.Field("workMode").Size(10)))
                .Add("topTechnologies", a => a.Terms(t => t.Field("technologies").Size(10)))), ct);

        if (!response.IsValidResponse)
            throw new SearchUnavailableException("As estatísticas estão temporariamente indisponíveis (Elasticsearch).");

        // Todos os enums presentes (0 quando aplicável) → bySeniority/byWorkMode somam totalOpenJobs.
        var bySeniority = InitEnumCounts("junior", "pleno", "senior");
        var byWorkMode = InitEnumCounts("remoto", "hibrido", "presencial");
        FillBuckets(response.Aggregations, "bySeniority", bySeniority);
        FillBuckets(response.Aggregations, "byWorkMode", byWorkMode);

        var top = new List<TechnologyCount>();
        var techAgg = response.Aggregations?.GetStringTerms("topTechnologies");
        if (techAgg is not null)
        {
            foreach (var bucket in techAgg.Buckets)
            {
                var key = bucket.Key.Value as string ?? bucket.Key.ToString();
                if (key is not null) top.Add(new TechnologyCount(key, bucket.DocCount));
            }
        }

        return new JobsStats(response.Total, bySeniority, byWorkMode, top);
    }

    private static Dictionary<string, long> InitEnumCounts(params string[] keys) =>
        keys.ToDictionary(k => k, _ => 0L);

    private static void FillBuckets(AggregateDictionary? aggs, string name, Dictionary<string, long> target)
    {
        var terms = aggs?.GetStringTerms(name);
        if (terms is null) return;
        foreach (var bucket in terms.Buckets)
        {
            var key = bucket.Key.Value as string ?? bucket.Key.ToString();
            if (key is not null) target[key] = bucket.DocCount;
        }
    }
}
