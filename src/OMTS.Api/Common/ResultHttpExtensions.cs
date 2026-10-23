using Microsoft.AspNetCore.Mvc;
using OMTS.Application.Common.Results;

namespace OMTS.Api.Common;

/// <summary>Turns use-case results into HTTP responses: success via the given callback, errors as problem details.</summary>
internal static class ResultHttpExtensions
{
    public static IActionResult ToActionResult<T>(this Result<T> result, Func<T, IActionResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value!) : result.Error!.ToProblem();

    public static ObjectResult ToProblem(this AppError error)
    {
        var problem = ProblemResults.Create(StatusOf(error.Kind), error.Message);
        if (error.Details is not null && problem.Value is ProblemDetails details)
        {
            foreach (var (key, value) in error.Details)
            {
                details.Extensions[key] = value;
            }
        }

        return problem;
    }

    private static int StatusOf(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Locked => StatusCodes.Status423Locked,
        ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorKind.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorKind.BadGateway => StatusCodes.Status502BadGateway,
        ErrorKind.Internal => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError,
    };
}
