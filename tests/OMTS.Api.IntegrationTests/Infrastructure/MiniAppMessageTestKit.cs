using System.Text.Json;

namespace OMTS.Api.IntegrationTests.Infrastructure;

public static class MiniAppMessageTestKit
{
    /// <summary>The existing Mini App can still read legacyCode while web clients use the SRS message catalogue.</summary>
    public static async Task<string?> CompatibilityCodeAsync(this HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("legacyCode", out var legacy) && legacy.ValueKind == JsonValueKind.String
            ? legacy.GetString() : json.RootElement.GetProperty("code").GetString();
    }
}
