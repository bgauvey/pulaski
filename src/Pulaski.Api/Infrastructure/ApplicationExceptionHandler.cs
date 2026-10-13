using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Pulaski.Application.Errors;
using Pulaski.Domain;

namespace Pulaski.Api.Infrastructure;

/// <summary>Turns application and domain exceptions into RFC 9457 problem responses.</summary>
internal sealed class ApplicationExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            ValidationException ex => new ValidationProblemDetails(
                ex.Errors.ToDictionary(e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = ex.Message,
            },
            NotFoundException ex => Problem(StatusCodes.Status404NotFound, "Not found", ex.Message),
            ConflictException ex => Problem(StatusCodes.Status409Conflict, "Conflict", ex.Message),
            DomainRuleException ex => Problem(StatusCodes.Status422UnprocessableEntity, "Rule violated", ex.Message),
            _ => null,
        };

        if (problem is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails Problem(int status, string title, string detail) =>
        new() { Status = status, Title = title, Detail = detail };
}
