using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace OMTS.Api.Filters;

/// <summary>
/// A multipart upload whose Content-Length is above the endpoint's <see cref="RequestSizeLimitAttribute"/> answers
/// like every other invalid field: 400 MSG01 naming the endpoint's file field, through the global invalid
/// model state response. Without this, Kestrel refuses the body while model binding reads the form, which
/// reports the anonymous field "Request" (TestServer has no body size limit, so only a Kestrel host shows it).
/// The body is never read, so a client sending <c>Expect: 100-continue</c> does not upload it at all.
/// </summary>
public sealed class OversizedUploadFilter(IOptions<ApiBehaviorOptions> apiBehavior) : IAsyncResourceFilter, IOrderedFilter
{
    /// <summary>After the <see cref="RequestSizeLimitAttribute"/> filter (900), which sets the endpoint's limit.</summary>
    public int Order => 1000;

    private const long DrainFactor = 2;

    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var bodySize = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize?.MaxRequestBodySize is not { } limit || request.ContentLength is not { } length || length <= limit || !request.HasFormContentType)
        {
            return next();
        }

        // Kestrel discards the unread body after the response. A browser that sent the whole body (no
        // Expect: 100-continue) only sees the 400 if that succeeds, so a body up to twice the limit may be
        // discarded; anything larger still closes the connection.
        if (!bodySize.IsReadOnly && length <= DrainFactor * limit)
        {
            bodySize.MaxRequestBodySize = length;
        }

        context.ModelState.AddModelError(FileField(context.ActionDescriptor), $"The request body is larger than {limit} bytes.");
        context.Result = apiBehavior.Value.InvalidModelStateResponseFactory(context);
        return Task.CompletedTask;
    }

    // The multipart name the client sends: a file parameter ("file", "files", "photo") or a file property of the
    // bound form (RecordWaterMeasurementForm.Photo → "photo").
    private static string FileField(ActionDescriptor action)
    {
        foreach (var parameter in action.Parameters.OfType<ControllerParameterDescriptor>())
        {
            if (IsFile(parameter.ParameterType))
            {
                return parameter.BindingInfo?.BinderModelName ?? parameter.Name;
            }

            if (parameter.ParameterType.GetProperties().FirstOrDefault(p => IsFile(p.PropertyType)) is { } property)
            {
                return JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            }
        }

        return "file";
    }

    private static bool IsFile(Type type) => typeof(IFormFile).IsAssignableFrom(type) || typeof(IEnumerable<IFormFile>).IsAssignableFrom(type);
}
