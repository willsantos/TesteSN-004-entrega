using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.JobsCore;
using TechJobs.Api.JobsCore.Persistence;
using TechJobs.Api.Services;

namespace TechJobs.Api.Slices.CreateJob;

public static class CreateJobEndpoint
{
    public static void MapCreateJobEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/jobs", async (HttpRequest request, JobStore store, IndexingQueue indexingQueue) =>
        {
            var body = await ReadJobRequestAsync(request);
            var validated = ValidateOrThrow(body);
            // Escrita confirmada no PostgreSQL ANTES de qualquer resposta de sucesso.
            // A indexação é eventual (outbox + worker): nunca bloqueia nem falha o POST.
            var job = await store.CreateAsync(Guid.NewGuid(), validated, DateTime.UtcNow, request.HttpContext.RequestAborted);
            indexingQueue.Wake(job.Id);
            return Results.Created($"/api/jobs/{job.Id}", JobResponse.From(job));
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
