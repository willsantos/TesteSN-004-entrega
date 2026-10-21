using System.Text.Json;
using TechJobs.Api.Contracts;
using TechJobs.Api.Domain;
using TechJobs.Api.JobsCore;
using TechJobs.Api.Services;

namespace TechJobs.Api.Endpoints;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapPost("", async (HttpRequest request, JobsService jobs) =>
        {
            var body = await ReadJobRequestAsync(request);
            var validated = ValidateOrThrow(body);
            var job = await jobs.CreateAsync(validated, request.HttpContext.RequestAborted);
            return Results.Created($"/api/jobs/{job.Id}", JobResponse.From(job));
        });

        group.MapPut("/{id:guid}", async (Guid id, HttpRequest request, JobsService jobs) =>
        {
            var body = await ReadJobRequestAsync(request);
            var validated = ValidateOrThrow(body);
            var job = await jobs.ReplaceAsync(id, validated, request.HttpContext.RequestAborted);
            return Results.Ok(JobResponse.From(job));
        });

        group.MapGet("/{id:guid}", async (Guid id, JobsService jobs) =>
        {
            var json = await jobs.GetJobJsonAsync(id);
            return Results.Content(json, "application/json");
        });

        group.MapGet("/search", async (HttpRequest request, JobsService jobs) =>
        {
            var parameters = SearchRequestParser.Parse(request.Query, out var errors);
            if (errors.Count > 0)
                throw new ValidationException(string.Join(' ', errors));
            var json = await jobs.SearchJsonAsync(parameters, request.HttpContext.RequestAborted);
            return Results.Content(json, "application/json");
        });

        group.MapGet("/stats", async (JobsService jobs) =>
            Results.Json(await jobs.StatsAsync()));
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
