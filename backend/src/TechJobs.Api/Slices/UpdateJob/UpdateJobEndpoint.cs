using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Platform.Runtime;
using TechJobs.Api.Services;

namespace TechJobs.Api.Slices.UpdateJob;

public static class UpdateJobEndpoint
{
    public static void MapUpdateJobEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/jobs/{id:guid}", async (Guid id, HttpRequest request, JobStore store, CacheService cache, IndexingQueue indexingQueue) =>
        {
            var body = await ReadJobRequestAsync(request);
            var validated = ValidateOrThrow(body);
            var job = await store.ReplaceAsync(id, validated, request.HttpContext.RequestAborted)
                ?? throw new JobNotFoundException(id);
            // Invalidação do cache de detalhe APÓS o commit — o valor antigo não volta.
            await cache.DeleteKeyAsync(CacheKeys.DetailsKey(id), CacheCounters.Kind.Details);
            indexingQueue.Wake(id);
            return Results.Ok(JobResponse.From(job));
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
