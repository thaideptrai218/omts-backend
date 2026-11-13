using System.Text.Json.Serialization;
using OMTS.Api.Commands;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Api.Services;
using OMTS.Application;
using OMTS.Application.Common.Interfaces;
using OMTS.Infrastructure;

if (EnvironmentVariablesConflict.Refusal(
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")) is { } environmentConflict)
{
    Console.Error.WriteLine(environmentConflict);
    return 1;
}

var builder = WebApplication.CreateBuilder(BootstrapCommand.HostArguments(args));

builder.Services.AddApplication();
builder.Services.Configure<OMTS.Application.Features.Testing.Options.TestRoleOptions>(
    builder.Configuration.GetSection("Development:TestRoles"));
builder.Services.PostConfigure<OMTS.Application.Features.Testing.Options.TestRoleOptions>(options =>
{
    if (!builder.Environment.IsDevelopment()) options.Enabled = false;
});
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<ICurrentEmployee, CurrentEmployee>();

builder.Services.AddOmtsForwardedHeaders(builder.Configuration);
builder.Services.AddOmtsBffClientHeaders(builder.Configuration);
builder.Services.AddOmtsCors(builder.Configuration);
builder.Services.AddOmtsRateLimiting(builder.Configuration);
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers(options =>
    {
        options.Filters.Add<AccountStateGateFilter>();
        options.Filters.Add<OversizedUploadFilter>();
    })
    // Enums travel by name ("Sms", "Web", "View"); numbers are still accepted.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    // Unreadable values answer 400 MSG-GEN-01 like every other invalid field.
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = InvalidModelStateResponse.Create);
// A time sent without a UTC offset is Vietnam time (UTC+7), in JSON bodies and in Swagger.
builder.Services.AddOmtsBusinessTime();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOmtsSwagger();

var app = builder.Build();

// One-time deployment step; runs instead of the web host (see README "Deployment").
if (BootstrapCommand.IsInvoked(args))
{
    return await BootstrapCommand.RunAsync(app.Services, args);
}

// First: the client IP of a request through a trusted proxy is what the rate limits and logs see. The BFF's secret is
// checked before forwarded headers can replace the connection address; what it supplied is applied after them.
app.UseOmtsBffClientCapture();
app.UseForwardedHeaders();
app.UseOmtsBffClientApply();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Redirects only when an HTTPS port is known (an HTTPS endpoint, or ASPNETCORE_HTTPS_PORTS / https_port); the
// Docker image listens on plain HTTP 8080 only, so its requests are served as they are.
app.UseHttpsRedirection();

// Before authentication, so browser preflight requests are answered without a token.
app.UseOmtsCors();
// The rate limiter follows authentication: the employee login code limit is per signed-in account (see RateLimitingExtensions).
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapOmtsHealthChecks();

app.Run();
return 0;

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;
