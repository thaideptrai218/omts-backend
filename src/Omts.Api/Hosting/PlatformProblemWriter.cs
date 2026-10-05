namespace Omts.Api.Hosting;

public sealed class PlatformProblemWriter : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        PlatformProblems.Customize(context);
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
            context.ProblemDetails, options: null, contentType: "application/problem+json",
            cancellationToken: context.HttpContext.RequestAborted));
    }
}
