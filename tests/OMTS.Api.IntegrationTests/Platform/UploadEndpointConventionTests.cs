using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Filters;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// Every action that takes a file states its request size limit and the field its MSG01 names, so an upload
/// above the limit answers like the endpoint's own file rules (OversizedUploadFilter).
/// </summary>
public class UploadEndpointConventionTests
{
    [Fact]
    public void Every_upload_action_has_a_request_size_limit_and_an_upload_field()
    {
        var uploads = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetParameters().Any(p => TakesFile(p.ParameterType)))
            .ToList();

        Assert.NotEmpty(uploads);
        Assert.All(uploads, action =>
        {
            var name = $"{action.DeclaringType!.Name}.{action.Name}";
            Assert.True(action.GetCustomAttribute<RequestSizeLimitAttribute>() is not null, $"{name} has no [RequestSizeLimit]");
            Assert.True(action.GetCustomAttribute<UploadFieldAttribute>() is not null, $"{name} has no [UploadField]");
        });
    }

    private static bool TakesFile(Type type) =>
        IsFile(type) || (type.IsClass && type != typeof(string) && type.GetProperties().Any(p => IsFile(p.PropertyType)));

    private static bool IsFile(Type type) => typeof(IFormFile).IsAssignableFrom(type) || typeof(IEnumerable<IFormFile>).IsAssignableFrom(type);
}
