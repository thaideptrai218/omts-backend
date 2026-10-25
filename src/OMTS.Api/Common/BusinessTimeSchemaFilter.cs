using System.Reflection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OMTS.Api.Common;

/// <summary>
/// Documents the business time rule on every date-time of the Swagger schema (each <see cref="DateTimeOffset"/>
/// and each field marked <see cref="BusinessTimeAttribute"/>), in requests and responses alike.
/// </summary>
internal sealed class BusinessTimeSchemaFilter : ISchemaFilter
{
    public const string Rule =
        "ISO 8601 date and time. A value without a UTC offset (\"2026-10-20T08:00:00\") is read as Vietnam time (UTC+7), " +
        "never as the server's time zone or UTC; a value with \"Z\" or an explicit offset (\"+07:00\") keeps it. " +
        "Responses always carry an offset.";

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var type = Nullable.GetUnderlyingType(context.Type) ?? context.Type;
        var marked = context.MemberInfo?.IsDefined(typeof(BusinessTimeAttribute)) == true;
        if (type != typeof(DateTimeOffset) && !marked)
        {
            return;
        }

        schema.Format ??= "date-time";
        schema.Description = string.IsNullOrWhiteSpace(schema.Description) ? Rule : $"{schema.Description} {Rule}";
    }
}
