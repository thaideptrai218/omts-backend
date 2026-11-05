namespace OMTS.Api.Commands;

/// <summary>
/// .NET 8's <c>WebApplication.CreateBuilder</c> lets <c>DOTNET_ENVIRONMENT</c> beat <c>ASPNETCORE_ENVIRONMENT</c>, so a
/// stray variable would turn Production into Development (Swagger, dev inbox) with nothing to show for it. Both set and
/// different stops the seed command and the API before anything starts. Environment names ignore letter case in .NET.
/// </summary>
internal static class EnvironmentVariablesConflict
{
    public static string? Refusal(string? aspNetCore, string? dotNet) =>
        string.IsNullOrEmpty(aspNetCore) || string.IsNullOrEmpty(dotNet) || string.Equals(aspNetCore, dotNet, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Refused to start: ASPNETCORE_ENVIRONMENT is '{aspNetCore}' and DOTNET_ENVIRONMENT is '{dotNet}'. Remove one, or make them the same.";
}
