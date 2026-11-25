using System.Text.Encodings.Web;
using System.Text.Json;

namespace OMTS.Application.Features.Farming.Zones;

/// <summary>The GeoJSON text of <see cref="Domain.Entities.Farming.FarmingZone.BoundaryGeoJson"/>, in the camelCase of the answers.</summary>
internal static class ZoneBoundaryJson
{
    // Stored text only (never written into HTML), so names such as "Khu Đông" stay unescaped.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string? Write(ZoneBoundary? boundary) => boundary is null ? null : JsonSerializer.Serialize(boundary, Options);

    public static ZoneBoundary? Read(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ZoneBoundary>(json, Options);
}
