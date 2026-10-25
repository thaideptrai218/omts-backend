using Microsoft.OpenApi.Models;

namespace OMTS.Api.Extensions;

/// <summary>
/// Swagger (Development only, see Program): bearer login, and the XML comments of the controllers and of the request
/// and response types, with their <c>&lt;example&gt;</c> values (e.g. the multipart license forms).
/// </summary>
public static class SwaggerSetup
{
    // The API project documents the endpoints, the Application project the request and response types.
    private static readonly string[] DocumentedAssemblies = ["OMTS.Api", "OMTS.Application"];

    public static IServiceCollection AddOmtsSwagger(this IServiceCollection services) => services.AddSwaggerGen(options =>
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

        foreach (var assembly in DocumentedAssemblies)
        {
            // Built with GenerateDocumentationFile; a missing file only leaves Swagger without descriptions.
            var path = Path.Combine(AppContext.BaseDirectory, $"{assembly}.xml");
            if (File.Exists(path))
            {
                options.IncludeXmlComments(path, includeControllerXmlComments: true);
            }
        }
    });
}
