using Crypton.Core.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Crypton.Api.Infrastructure;

/// <summary>Maps exceptions to RFC 7807 problem responses with a stable <c>code</c> extension.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case AppException app:
                problem = Create(app.StatusCode, app.Code, app.Message);
                if (app.Details is not null)
                {
                    problem.Extensions["details"] = app.Details;
                }

                break;

            case UnauthorizedAccessException:
                problem = Create(StatusCodes.Status401Unauthorized, "unauthorized", "Please sign in again.");
                break;

            case BadHttpRequestException bad:
                problem = Create(bad.StatusCode, "bad_request", bad.Message);
                break;

            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                return true;

            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
                problem = Create(StatusCodes.Status500InternalServerError, "internal_error", "Something went wrong on our side. Please try again.");
                break;
        }

        problem.Extensions["traceId"] = context.TraceIdentifier;
        context.Response.StatusCode = problem.Status ?? 500;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }

    public static ProblemDetails Create(int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Type = $"https://docs.crypton.local/errors/{code}" };
        problem.Extensions["code"] = code;
        return problem;
    }
}
