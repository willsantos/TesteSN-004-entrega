using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;
using TechJobs.Api.Domain;
using TechJobs.Api.Infrastructure.Elastic;

namespace TechJobs.Api.Infrastructure;

public sealed record SearchResult(long Total, IReadOnlyList<JobDoc> Items);

/// <summary>
/// Projeção de busca no índice `techjobs-v1`. Toda operação checa a falha simulada
/// primeiro e trata resposta inválida como indisponibilidade — a leitura pública
/// devolve 503; a indexação (worker) apenas deixa o evento pendente no outbox.
/// </summary>
public sealed class ElasticService
{
    public const string IndexName = "techjobs-v1";
    private const int BulkChunkSize = 2000;

    private readonly ElasticsearchClient _client;
    private readonly FaultState _faults;
    private readonly ILogger<ElasticService> _logger;

    public ElasticService(ElasticsearchClient client, FaultState faults, ILogger<ElasticService> logger)
    {
        _client = client;
        _faults = faults;
        _logger = logger;
    }

    private void EnsureAvailable()
    {
        if (_faults.ElasticDown)
            throw new SearchUnavailableException("A busca está temporariamente indisponível (Elasticsearch indisponível).");
    }

    /// <summary>Cria o índice com mapping explícito se ainda não existir. Idempotente.</summary>
    public async Task<bool> EnsureIndexAsync(CancellationToken ct = default)
    {
        var exists = await _client.Indices.ExistsAsync(IndexName, ct);
        if (exists.Exists) return true;

        var response = await _client.Indices.CreateAsync<JobDoc>(IndexName, c => c
            .Settings(s => s
                .NumberOfShards(1)
                .NumberOfReplicas(0))
            .Mappings(m => m
                .Properties(p => p
                    .Keyword("id")
                    .Text("title", t => t.Analyzer("portuguese"))
                    .Text("description", t => t.Analyzer("portuguese"))
                    .Text("company", t => t.Fields(f => f.Keyword("raw")))
                    .Keyword("technologies")
                    .Keyword("techKeys")
                    .Keyword("seniority")
                    .Keyword("workMode")
                    .Keyword("city")
                    .Keyword("status")
                    .DoubleNumber("salaryMin")
                    .DoubleNumber("salaryMax")
                    .Date("publishedAt")
                    .Date("updatedAt"))), ct);

        if (!response.IsValidResponse && !IsIndexAlreadyExists(response.DebugInformation))
        {
            _logger.LogWarning("Criação do índice {Index} falhou: {Error}", IndexName, response.DebugInformation);
            return false;
        }
        return true;
    }

    private static bool IsIndexAlreadyExists(string? debug) =>
        debug?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true
        || debug?.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase) == true;

    public async Task DropAndCreateIndexAsync(CancellationToken ct = default)
    {
        EnsureAvailable();
        await _client.Indices.DeleteAsync(IndexName, ct);
        var created = await EnsureIndexAsync(ct);
        if (!created) throw new SearchUnavailableException("Falha ao recriar o índice de busca.");
    }

    public async Task<long> DeleteAllDocumentsAsync(CancellationToken ct = default)
    {
        EnsureAvailable();
        var response = await _client.DeleteByQueryAsync(IndexName, d => d
            .Query(q => q.MatchAll(m => { }))
            .Conflicts(Conflicts.Proceed), ct);
        if (!response.IsValidResponse)
            throw new SearchUnavailableException("Falha ao limpar o índice de busca.");
        return response.Deleted ?? 0;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        EnsureAvailable();
        var response = await _client.Indices.RefreshAsync(IndexName, ct);
        if (!response.IsValidResponse)
            throw new SearchUnavailableException("Falha ao aplicar refresh do índice.");
    }

    public async Task IndexJobAsync(Job job, CancellationToken ct = default)
    {
        EnsureAvailable();
        var response = await _client.IndexAsync(JobDoc.From(job), i => i.Index(IndexName).Id(job.Id), ct);
        if (!response.IsValidResponse)
            throw new SearchUnavailableException("Falha ao indexar vaga no Elasticsearch.");
    }

    /// <summary>Bulk index com id explícito (id do documento = id da vaga → upsert, nunca duplica).</summary>
    public async Task IndexManyAsync(IReadOnlyList<Job> jobs, CancellationToken ct = default)
    {
        if (jobs.Count == 0) return;
        EnsureAvailable();
        foreach (var chunk in jobs.Chunk(BulkChunkSize))
        {
            var operations = new List<IBulkOperation>(chunk.Length);
            foreach (var job in chunk)
            {
                operations.Add(new BulkIndexOperation<JobDoc>(JobDoc.From(job))
                {
                    Id = job.Id,
                    Index = IndexName,
                });
            }
            var request = new BulkRequest
            {
                Operations = new BulkOperationsCollection([.. operations]),
            };
            var response = await _client.BulkAsync(request, ct);
            if (!response.IsValidResponse || response.Errors)
                throw new SearchUnavailableException("Falha ao indexar lote de vagas no Elasticsearch.");
        }
    }

    public async Task<SearchResult> SearchAsync(SearchQueryParams p, CancellationToken ct = default)
    {
        EnsureAvailable();

        var response = await _client.SearchAsync<JobDoc>(s => BuildSearch(s, p), ct);
        if (!response.IsValidResponse)
            throw new SearchUnavailableException("A busca está temporariamente indisponível (Elasticsearch).");

        var items = response.Hits
            .Where(h => h.Source is not null)
            .Select(h => h.Source!)
            .ToList();
        return new SearchResult(response.Total, items);
    }

    private static SearchRequestDescriptor<JobDoc> BuildSearch(SearchRequestDescriptor<JobDoc> s, SearchQueryParams p)
    {
        var filters = new List<Action<QueryDescriptor<JobDoc>>>
        {
            // Regra de domínio: apenas vagas abertas aparecem na busca e nas estatísticas.
            f => f.Term(t => t.Field("status").Value("aberta")),
        };
        if (p.Seniority is not null)
            filters.Add(f => f.Term(t => t.Field("seniority").Value(p.Seniority.Value.ToApi())));
        if (p.WorkMode is not null)
            filters.Add(f => f.Term(t => t.Field("workMode").Value(p.WorkMode.Value.ToApi())));
        foreach (var tech in p.Technologies)
            filters.Add(f => f.Term(t => t.Field("techKeys").Value(tech)));
        // Interseção de intervalos, inclusiva; range em campo ausente não casa → vagas sem salário caem fora.
        if (p.MinSalary is not null)
            filters.Add(f => f.Range(r => r.NumberRange(n => n.Field("salaryMax").Gte((double)p.MinSalary.Value))));
        if (p.MaxSalary is not null)
            filters.Add(f => f.Range(r => r.NumberRange(n => n.Field("salaryMin").Lte((double)p.MaxSalary.Value))));

        s.Index(IndexName)
         .From((p.Page - 1) * p.PageSize)
         .Size(p.PageSize)
         .TrackTotalHits(new TrackHits(true))
         .Query(q => q.Bool(b =>
         {
             if (p.HasQueryText)
             {
                 // Título pesa mais que descrição (boost 3x); múltiplos termos: semântica E.
                 b.Must(m => m.MultiMatch(mm => mm
                     .Fields(new[] { "title^3", "description" })
                     .Query(p.Q)
                     .Operator(Operator.And)));
             }
             else
             {
                 b.Must(m => m.MatchAll(a => { }));
             }
             // Uma única chamada: Filter em descriptor sobrescreve, não acumula.
             b.Filter([.. filters]);
         }));

        var effectiveSort = p.EffectiveSort;
        if (effectiveSort == SearchQueryParams.SortNewest)
        {
            s.Sort(
                so => so.Field("publishedAt", f => f.Order(SortOrder.Desc)),
                so => so.Field("id", f => f.Order(SortOrder.Asc)));
        }
        else if (effectiveSort == SearchQueryParams.SortSalaryDesc)
        {
            s.Sort(
                so => so.Field("salaryMax", f => f.Order(SortOrder.Desc).Missing("_last")),
                so => so.Field("id", f => f.Order(SortOrder.Asc)));
        }
        // relevance com q: ordenação por _score (default) — sem cláusula de sort.
        return s;
    }

    public async Task<JobsStats> StatsAsync(CancellationToken ct = default)
    {
        EnsureAvailable();

        var response = await _client.SearchAsync<JobDoc>(s => s
            .Index(IndexName)
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
