using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Omts.Api.Hosting;

public static class PlatformProblems
{
    public static void Customize(ProblemDetailsContext context)
    {
        int status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
        context.ProblemDetails.Status = status;
        context.ProblemDetails.Type = "about:blank";
        context.ProblemDetails.Title = ReasonPhrases.GetReasonPhrase(status);
        context.ProblemDetails.Detail = null;
        context.ProblemDetails.Instance = null;
        context.ProblemDetails.Extensions.Clear();
        context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    }

    public static async Task WriteAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = status }
        });
    }
}
