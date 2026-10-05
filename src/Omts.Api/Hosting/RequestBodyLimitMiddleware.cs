using Microsoft.Extensions.Options;

namespace Omts.Api.Hosting;

public sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<HostingOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Reject known oversized bodies before endpoint processing. Kestrel limits streamed body reads.
        if (context.Request.ContentLength > options.Value.MaxRequestBodyBytes)
        {
            await PlatformProblems.WriteAsync(context, StatusCodes.Status413PayloadTooLarge);
            return;
        }

        await next(context);
    }
}
