using Microsoft.AspNetCore.Mvc;

namespace OMTS.Api;

/// <summary>Error responses carry the SRS message code (MSGxx) so clients can show the Vietnamese text.</summary>
internal static class Problems
{
    public static ObjectResult Create(int status, string code, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
