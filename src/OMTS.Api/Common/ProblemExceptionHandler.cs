using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Messages;

namespace OMTS.Api.Common;

/// <summary>
/// Last line of defence: an unhandled concurrency conflict becomes 409 MSG06, a duplicate 409 MSG03, a file
/// store failure 502 MSG05, anything else 500 with a generic problem body. Exception details go to the log only, never to
/// the client.
/// </summary>
internal sealed class ProblemExceptionHandler(ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, message) = exception switch
        {
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, UserMessages.ConcurrentUpdateByAnotherUser),
            // Already logged by the storage provider.
            FileStorageUnavailableException => (StatusCodes.Status502BadGateway, UserMessages.FileOperationFailed),
            // The database message names the duplicate value (a phone, an email), so it is not logged here.
            // The save-error log of EF Core and the log of the exception middleware are switched off in
            // appsettings.json (Microsoft.EntityFrameworkCore.Update, ...ExceptionHandlerMiddleware); every 500 is logged below.
            DuplicateRecordException => (StatusCodes.Status409Conflict, UserMessages.AlreadyExists("Record")),
            _ => (StatusCodes.Status500InternalServerError, UserMessages.UnexpectedError),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        var problem = new ProblemDetails { Status = status, Title = message.Code, Detail = message.Text };
        problem.Extensions["code"] = message.Code;
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken);
        return true;
    }
}
