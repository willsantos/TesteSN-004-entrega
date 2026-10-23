using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Elastic;
using TechJobs.Api.Platform.Runtime;

namespace TechJobs.Api.Slices.UpdateJob;

public static class UpdateJobEndpoint
{
    public static void MapUpdateJobEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/jobs/{id:guid}", async (Guid id, HttpRequest request, JobStore store, CacheService cache, IndexingQueue indexingQueue, SingleFlight singleFlight) =>
        {
            var body = await ReadJobRequestAsync(request);
            var validated = ValidateOrThrow(body);
            var key = CacheKeys.DetailsKey(id);
            var aborted = request.HttpContext.RequestAborted;

            // Commit + invalidação coalescidos com o preenchimento de detalhe da MESMA
            // vaga na instância: fecha a janela GET×PUT (o fill nunca regrava valor
            // anterior ao commit). Seguidor de OUTRO PUT (payload diferente) revalida:
            // repete até executar a própria escrita — a última escrita sempre vence.
            while (true)
            {
                var (job, executedFactory) = await singleFlight.CoalesceAsync(key, async _ =>
                {
                    var replaced = await store.ReplaceAsync(id, validated, aborted)
                        ?? throw new JobNotFoundException(id);
                    // Invalidação do cache de detalhe APÓS o commit — o valor antigo não volta.
                    await cache.DeleteKeyAsync(key, CacheCounters.Kind.Details);
                    return replaced;
                }, aborted);

                if (executedFactory)
                {
                    indexingQueue.Wake(id);
                    return Results.Ok(JobResponse.From(job));
                }
            }
        });
    }

    private static async Task<JobRequest> ReadJobRequestAsync(HttpRequest request)
    {
        try
        {
            var body = await request.ReadFromJsonAsync<JobRequest>();
            return body ?? throw new ValidationException("Corpo da requisição é obrigatório (JSON da vaga).");
        }
        catch (JsonException)
        {
            throw new ValidationException("Corpo da requisição inválido: JSON malformado ou campo com tipo incorreto.");
        }
        catch (BadHttpRequestException)
        {
            throw new ValidationException("Corpo da requisição inválido: JSON malformado ou campo com tipo incorreto.");
        }
    }

    private static ValidatedJob ValidateOrThrow(JobRequest body)
    {
        var validated = JobValidator.Validate(body, out var errors);
        if (validated is null)
            throw new ValidationException(string.Join(' ', errors));
        return validated;
    }
}
