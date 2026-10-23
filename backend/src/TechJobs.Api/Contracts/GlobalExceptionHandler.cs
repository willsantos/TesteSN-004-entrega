using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace TechJobs.Api.Contracts;

/// <summary>
/// Converte exceções em Problem Details (RFC 9457): 400 validação, 404 vaga ausente,
/// 503 busca indisponível, 500 inesperado (sem stack trace).
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, type, detail) = exception switch
        {
            ValidationException v => (StatusCodes.Status400BadRequest, "Validation failed", "https://techjobs.dev/errors/validation", v.Detail),
            JobNotFoundException n => (StatusCodes.Status404NotFound, "Job not found", "https://techjobs.dev/errors/job-not-found", $"Vaga {n.JobId} não encontrada."),
            SearchUnavailableException s => (StatusCodes.Status503ServiceUnavailable, "Service unavailable", "https://techjobs.dev/errors/search-unavailable", s.Detail),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "https://techjobs.dev/errors/unexpected", "Ocorreu um erro inesperado. Tente novamente."),
        };

        if (status == 500)
            logger.LogError(exception, "Erro não tratado em {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        // Log sem o conteúdo do detail: ele pode ecoar payload enviado pelo usuário
        // (ex.: mensagens de validação) e não deve parar em logs. O detail segue
        // intacto apenas no CORPO da resposta Problem Details.
        else
            logger.LogInformation("Respondendo {Status} para {Method} {Path}", status, httpContext.Request.Method, httpContext.Request.Path);

        var payload = new ProblemDetailsPayload(type, title, status, detail);
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
        return true;
    }

    private sealed record ProblemDetailsPayload(string Type, string Title, int Status, string Detail);
}
