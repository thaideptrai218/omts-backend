using Microsoft.AspNetCore.Mvc;
using OMTS.Application.Common.Messages;

namespace OMTS.Api.Common;

/// <summary>Error responses carry the SRS message code (MSGxx) so clients can show the Vietnamese text.</summary>
internal static class ProblemResults
{
    public static ObjectResult Create(int status, string code, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    public static ObjectResult Create(int status, UserMessage message) => Create(status, message.Code, message.Text);
}
