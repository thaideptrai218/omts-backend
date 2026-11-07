using Microsoft.AspNetCore.Mvc;
using OMTS.Application.Common.Messages;

namespace OMTS.Api.Common;

/// <summary>
/// Error responses carry the SRS message code (MSG-GEN-nn, MSG-&lt;feature&gt;-nn) as <c>title</c> and <c>code</c>, so
/// clients can show the Vietnamese text, and the old code (MSGnn) as <c>legacyCode</c> for one release when the
/// code changed.
/// </summary>
internal static class ProblemResults
{
    public static ObjectResult Create(int status, UserMessage message)
    {
        var problem = Problem(status, message);
        return new ObjectResult(problem) { StatusCode = status };
    }

    /// <summary>400 MSG-GEN-01 naming the field, also as the <c>field</c> extension.</summary>
    public static ObjectResult InvalidField(string field)
    {
        var problem = Problem(StatusCodes.Status400BadRequest, UserMessages.Invalid(field));
        problem.Extensions["field"] = field;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
    }

    public static ProblemDetails Problem(int status, UserMessage message)
    {
        var problem = new ProblemDetails { Status = status, Title = message.Code, Detail = message.Text };
        problem.Extensions["code"] = message.Code;
        if (message.LegacyCode is { } legacyCode)
        {
            problem.Extensions["legacyCode"] = legacyCode;
        }

        return problem;
    }
}
