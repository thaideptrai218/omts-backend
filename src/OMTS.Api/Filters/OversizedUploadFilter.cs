using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using OMTS.Api.Common;

namespace OMTS.Api.Filters;

/// <summary>
/// A multipart upload above the endpoint's <see cref="RequestSizeLimitAttribute"/> answers like every other invalid
/// file: 400 MSG01 on the endpoint's <see cref="UploadFieldAttribute"/>, through the global invalid model state
/// response. Without this, Kestrel refuses the body while model binding reads the form, which reports the
/// anonymous field "Request" (TestServer has no body size limit, so only a Kestrel host shows it).
/// <list type="bullet">
/// <item>With a Content-Length the body is never read, so a client sending <c>Expect: 100-continue</c> does not
/// upload it at all.</item>
/// <item>Without one (chunked), the form is read here and counted against the limit.</item>
/// <item>Either way Kestrel may read up to <see cref="DrainAllowance"/> more and discard it, so a browser that
/// sent the whole body still reads the answer; a larger body closes the connection.</item>
/// </list>
/// </summary>
public sealed class OversizedUploadFilter(IOptions<ApiBehaviorOptions> apiBehavior) : IAsyncResourceFilter, IOrderedFilter
{
    public const long DrainAllowance = 32 * 1024 * 1024;

    /// <summary>After the <see cref="RequestSizeLimitAttribute"/> filter (900), which sets the endpoint's limit.</summary>
    public int Order => 1000;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var bodySize = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize?.MaxRequestBodySize is not { } limit || bodySize.IsReadOnly || !request.HasFormContentType
            || !await IsTooLargeAsync(request, bodySize, limit))
        {
            await next();
            return;
        }

        var field = context.ActionDescriptor.EndpointMetadata.OfType<UploadFieldAttribute>().FirstOrDefault()?.Name ?? "Request";
        context.ModelState.AddModelError(field, $"The request body is larger than {limit} bytes.");
        context.Result = apiBehavior.Value.InvalidModelStateResponseFactory(context);
    }

    private static async Task<bool> IsTooLargeAsync(HttpRequest request, IHttpMaxRequestBodySizeFeature bodySize, long limit)
    {
        if (request.ContentLength is { } length)
        {
            if (length > limit && length <= limit + DrainAllowance)
            {
                bodySize.MaxRequestBodySize = length;
            }

            return length > limit;
        }

        // Kestrel's limit moves up by the allowance (so it can discard the rest); this stream keeps the endpoint's.
        bodySize.MaxRequestBodySize = limit + DrainAllowance;
        request.Body = new LengthLimitedStream(request.Body, limit);
        try
        {
            await request.ReadFormAsync(request.HttpContext.RequestAborted);
            return false;
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // The failed read is kept by the request's form feature: model binding reports it as before.
            return false;
        }
    }
}
