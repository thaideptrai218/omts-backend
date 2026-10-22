using Microsoft.OpenApi.Models;
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

builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers(options => options.Filters.Add<AccountStateGateFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var bearer = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token from POST /api/auth/login",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    options.AddSecurityDefinition("Bearer", bearer);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = [] });
});

var app = builder.Build();

// One-time deployment step; runs instead of the web host (see README "Deployment").
if (BootstrapCommand.IsInvoked(args))
{
    return await BootstrapCommand.RunAsync(app.Services, args);
}

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapOmtsHealthChecks();

app.Run();
return 0;

// Exposes the entry point to WebApplicationFactory in integration tests.
public partial class Program;
