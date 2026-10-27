using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;

namespace OMTS.Api.Extensions;

/// <summary>
/// One rule for every time the API reads: without a UTC offset it is Vietnam time (UTC+7, <c>BusinessClock</c>).
/// JSON bodies use <see cref="BusinessTimeJsonConverter"/>, multipart fields <see cref="InvariantFormValues"/>; no
/// endpoint takes a date and time from the query or the route (period filters are dates, yyyy-MM-dd).
/// </summary>
public static class BusinessTimeSetup
{
    public static IServiceCollection AddOmtsBusinessTime(this IServiceCollection services)
    {
        services.Configure<JsonOptions>(options => options.JsonSerializerOptions.Converters.Add(new BusinessTimeJsonConverter()));
        services.ConfigureSwaggerGen(options => options.SchemaFilter<BusinessTimeSchemaFilter>());
        return services;
    }
}
