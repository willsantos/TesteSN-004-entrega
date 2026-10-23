using System.Net;
using Xunit;

namespace TechJobs.IntegrationTests;

/// <summary>
/// Instância auxiliar com ENABLE_TEST_ENDPOINTS=false (TECHJOBS_API_NOADMIN, porta
/// 8085): os endpoints de /admin (ferramenta de teste) não existem e as rotas
/// públicas seguem intactas — a superfície administrativa não vaza para produção.
/// </summary>
public class AdminFlagOffTests
{
    [NoAdminApiFact]
    public async Task Admin_endpoints_retornam_404_sem_flag()
    {
        using var http = ApiClient.ForNoAdmin();
        using (var r = await http.GetAsync("/admin/cache-stats"))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await http.PostAsync("/admin/reset", null))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await http.PostAsync("/admin/seed", null))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await http.PostAsync("/admin/faults", null))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await http.PostAsync("/admin/indexing/drain", null))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using (var r = await http.PostAsync("/admin/indexing/reindex", null))
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [NoAdminApiFact]
    public async Task Rotas_publicas_funcionam_sem_flag()
    {
        using var http = ApiClient.ForNoAdmin();
        // A instância está viva — só o /admin sumiu.
        using (var health = await http.GetAsync("/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var search = await http.GetAsync("/api/jobs/search?page=1&pageSize=1"))
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
    }
}
