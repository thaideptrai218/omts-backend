using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Timeouts;
using Omts.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPlatform(builder.Configuration, builder.Environment);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError,
    SuppressDiagnosticsCallback = _ => false
});
app.UseStatusCodePages(async context =>
    await PlatformProblems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode));
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.UseRouting();
app.UseRequestTimeouts();
app.MapOperationalEndpoints();
app.Run();

// Expose the entry point to WebApplicationFactory without test routes in the application.
public partial class Program;
