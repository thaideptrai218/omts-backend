using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>A checked lane of a layout.</summary>
public sealed record LayoutLane(int LaneNumber, decimal Length, string MarkerA, string MarkerB);

/// <summary>
/// FARM-04 lane layout: validation, the JSON stored in <see cref="Lane.MeasurementMarkers"/> and the
/// snapshot kept in <see cref="RaftLayoutVersion.LayoutMap"/>, so old versions are still read correctly.
/// </summary>
public static class RaftLayout
{
    public const int MaxDrawingBytes = 50 * 1024;
    private const decimal MaxLength = 99_999_999.99m;
    // Stored text only (never written into HTML): Vietnamese marker names stay unescaped and fit the column.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Lanes 1…N with no gap or duplicate, length &gt; 0, both markers; null when invalid (field names added).</summary>
    internal static IReadOnlyList<LayoutLane>? Read(IReadOnlyList<LaneInput>? lanes, FieldCheck check)
    {
        if (lanes is not { Count: > 0 } || !lanes.Select(l => l.LaneNumber).Order().SequenceEqual(Enumerable.Range(1, lanes.Count)))
        {
            check.Invalid.Add("Lanes");
            return null;
        }

        var result = new List<LayoutLane>();
        foreach (var lane in lanes.OrderBy(l => l.LaneNumber))
        {
            var prefix = $"Lane {lane.LaneNumber}";
            check.Check(lane.Length is > 0 and <= MaxLength && decimal.Round(lane.Length.Value, 2) == lane.Length, $"{prefix} length");
            var markerA = check.Required(lane.MarkerA, $"{prefix} marker A", 50);
            var markerB = check.Required(lane.MarkerB, $"{prefix} marker B", 50);
            result.Add(new LayoutLane(lane.LaneNumber, lane.Length ?? 0, markerA, markerB));
        }

        return check.IsValid ? result : null;
    }

    /// <summary>The drawing as compact JSON text, or null; too large adds "Layout drawing".</summary>
    internal static string? ReadDrawing(JsonElement? drawing, FieldCheck check)
    {
        if (drawing is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value)
        {
            return null;
        }

        var text = value.GetRawText();
        check.Check(System.Text.Encoding.UTF8.GetByteCount(text) <= MaxDrawingBytes, "Layout drawing");
        return text;
    }

    public static string Markers(LayoutLane lane) => JsonSerializer.Serialize(new { markerA = lane.MarkerA, markerB = lane.MarkerB }, Json);

    public static (string? MarkerA, string? MarkerB) ReadMarkers(string? markers)
    {
        if (string.IsNullOrEmpty(markers))
        {
            return (null, null);
        }

        var node = JsonNode.Parse(markers);
        return (node?["markerA"]?.GetValue<string>(), node?["markerB"]?.GetValue<string>());
    }

    /// <summary>JSON snapshot of a version: every lane plus the drawing.</summary>
    public static string Snapshot(IReadOnlyList<LayoutLane> lanes, string? drawing)
    {
        var snapshot = new JsonObject
        {
            ["lanes"] = JsonSerializer.SerializeToNode(lanes, Json),
            ["layoutDrawing"] = drawing is null ? null : JsonNode.Parse(drawing),
        };
        // Same options as the markers: Vietnamese labels are stored as they are, not as \u escapes.
        return snapshot.ToJsonString(Json);
    }

    /// <summary>
    /// True when the request's drawing is the version's drawing, compared as JSON values: escaping and
    /// whitespace do not count as a change (a notes-only edit must not record a drawing change).
    /// </summary>
    public static bool SameDrawing(RaftLayoutVersion version, string? drawing)
    {
        var stored = version.LayoutMap is { } map ? JsonNode.Parse(map)?["layoutDrawing"] : null;
        return JsonNode.DeepEquals(stored, drawing is null ? null : JsonNode.Parse(drawing));
    }

    public static IReadOnlyList<LayoutLane> LanesOf(RaftLayoutVersion version) =>
        version.LayoutMap is { } map && JsonNode.Parse(map)?["lanes"] is JsonArray lanes
            ? lanes.Deserialize<List<LayoutLane>>(Json) ?? []
            : [];

    public static JsonElement? DrawingOf(RaftLayoutVersion? version) =>
        version?.LayoutMap is { } map && JsonNode.Parse(map)?["layoutDrawing"] is JsonNode drawing
            ? JsonSerializer.Deserialize<JsonElement>(drawing.ToJsonString())
            : null;

    /// <summary>True when the lanes differ from the raft's current lanes (count, lengths or markers).</summary>
    public static bool DiffersFrom(IReadOnlyList<LayoutLane> lanes, IEnumerable<Lane> current)
    {
        var existing = current.OrderBy(l => l.LaneNumber).ToList();
        return existing.Count != lanes.Count || existing.Zip(lanes).Any(pair =>
        {
            var (markerA, markerB) = ReadMarkers(pair.First.MeasurementMarkers);
            return pair.First.Length != pair.Second.Length || markerA != pair.Second.MarkerA || markerB != pair.Second.MarkerB;
        });
    }

    /// <summary>The version valid on <paramref name="date"/> (the newest one that had started), else the first.</summary>
    public static RaftLayoutVersion? VersionOn(IEnumerable<RaftLayoutVersion> versions, DateOnly date)
    {
        var all = versions.OrderBy(v => v.VersionNumber).ToList();
        return all.LastOrDefault(v => v.ValidFrom <= date) ?? all.FirstOrDefault();
    }
}
