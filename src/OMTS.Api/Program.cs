using System.Text.Json.Serialization;
using OMTS.Api.Commands;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Api.Services;
using OMTS.Application;
using OMTS.Application.Common.Interfaces;
using OMTS.Infrastructure;

var builder = WebApplication.CreateBuilder(BootstrapCommand.HostArguments(args));

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

builder.Services.AddOmtsForwardedHeaders(builder.Configuration);
builder.Services.AddOmtsCors(builder.Configuration);
builder.Services.AddOmtsRateLimiting(builder.Configuration);
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers(options => options.Filters.Add<AccountStateGateFilter>())
    // Enums travel by name ("Sms", "Web", "View"); numbers are still accepted.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    // Unreadable values answer 400 MSG01 like every other invalid field.
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = InvalidModelStateResponse.Create);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOmtsSwagger();

var app = builder.Build();

// One-time deployment step; runs instead of the web host (see README "Deployment").
if (BootstrapCommand.IsInvoked(args))
{
    return await BootstrapCommand.RunAsync(app.Services, args);
}

// First: the client IP of a request through a trusted proxy is what the rate limits and logs see.
app.UseForwardedHeaders();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Redirects only when an HTTPS port is known (an HTTPS endpoint, or ASPNETCORE_HTTPS_PORTS / https_port); the
// Docker image listens on plain HTTP 8080 only, so its requests are served as they are.
app.UseHttpsRedirection();

// Before the rate limiter and authentication, so browser preflight requests are answered without a token.
app.UseOmtsCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapOmtsHealthChecks();

app.Run();
return 0;

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;
