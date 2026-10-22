using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Slices.SearchJobs;

public sealed record SearchResult(long Total, IReadOnlyList<JobDoc> Items);

/// <summary>
/// Projeção de busca no índice `techjobs-v1`. A operação checa a falha simulada
/// primeiro e trata resposta inválida como indisponibilidade — a leitura pública
/// devolve 503.
/// </summary>
public sealed class SearchQueryRunner
{
    private readonly ElasticsearchClient _client;
    private readonly FaultState _faults;

    public SearchQueryRunner(ElasticsearchClient client, FaultState faults)
    {
        _client = client;
        _faults = faults;
    }

    private void EnsureAvailable()
    {
        if (_faults.ElasticDown)
            throw new SearchUnavailableException("A busca está temporariamente indisponível (Elasticsearch indisponível).");
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

        s.Index(ElasticIndex.IndexName)
         .From((p.Page - 1) * p.PageSize)
         .Size(p.PageSize)
         .TrackTotalHits(new TrackHits(true))
         .Query(q => q.Bool(b =>
         {
             if (p.HasQueryText)
             {
                 // Título pesa mais que descrição (boost 3x); múltiplos termos: semântica E
                 // distribuída entre título e descrição (cross_fields combina os dois campos
                 // com o analyzer `portuguese`) num único grupo: com Operator.And cada termo precisa
                 // aparecer em pelo menos um dos campos — semântica E distribuída entre título
                 // e descrição, e não mais restrita a um campo só (best_fields).
                 b.Must(m => m.MultiMatch(mm => mm
                     .Fields(new[] { "title^3", "description" })
                     .Query(p.Q)
                     .Type(TextQueryType.CrossFields)
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
}
