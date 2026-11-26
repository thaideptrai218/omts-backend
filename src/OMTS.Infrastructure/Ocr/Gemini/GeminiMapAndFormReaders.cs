using System.Text.Json;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Features.Farming.Rafts.FormScan;
using OMTS.Application.Features.Farming.Zones.BoundaryScan;
using OMTS.Application.Features.Scans;
using OMTS.Infrastructure.Ocr.Gemini.Prompts;

namespace OMTS.Infrastructure.Ocr.Gemini;

/// <summary>Shared by the scan readers: the answer is snake_case JSON shaped by the schema; a shape that does not fit is BAD_RESPONSE.</summary>
internal static class GeminiJsonShapes
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static ScanRead<T> Convert<T>(GeminiJsonResult answer, Func<JsonElement, T?> map) where T : class
    {
        if (!answer.Ok || answer.Json is not { } json)
        {
            return ScanRead<T>.Failed(answer.ErrorCode ?? OcrErrorCodes.BadResponse);
        }

        try
        {
            return map(json) is { } value ? ScanRead<T>.Read(value) : ScanRead<T>.Failed(OcrErrorCodes.BadResponse);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return ScanRead<T>.Failed(OcrErrorCodes.BadResponse);
        }
    }
}

/// <summary>Licence map reader on Gemini (Flash by default, 90 s): copies the info table (system, meridian, location, total area) and the corner tables as text.</summary>
internal sealed class GeminiZoneMapReader(IGeminiVisionClient gemini) : IZoneMapReader
{
    public async Task<ScanRead<RawZoneMap>> ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        GeminiJsonShapes.Convert(
            await gemini.GenerateJsonAsync(GeminiReader.ZoneMap, photo, mimeType, ZoneMapPrompt.Text, ZoneMapPrompt.Schema, cancellationToken),
            json => json.Deserialize<MapAnswer>(GeminiJsonShapes.Options) is { } map
                ? new RawZoneMap(
                    map.CoordinateSystem,
                    map.CentralMeridian,
                    (map.Zones ?? []).Select(z => new RawMapZone(
                        z.Name, z.WrittenArea, (z.Corners ?? []).Select(c => new RawMapCorner(c.Label, c.X, c.Y, c.Lat, c.Lon)).ToList())).ToList(),
                    map.Location,
                    map.WrittenTotalArea)
                : null);

    private sealed record MapAnswer(string? CoordinateSystem, string? CentralMeridian, string? Location, string? WrittenTotalArea, List<ZoneAnswer>? Zones);

    private sealed record ZoneAnswer(string? Name, string? WrittenArea, List<CornerAnswer>? Corners);

    private sealed record CornerAnswer(string? Label, string? X, string? Y, string? Lat, string? Lon);
}

/// <summary>Raft form reader on Gemini (Lite by default, 45 s): copies the labelled fields as text.</summary>
internal sealed class GeminiRaftFormReader(IGeminiVisionClient gemini) : IRaftFormReader
{
    public async Task<ScanRead<RaftFormReading>> ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        GeminiJsonShapes.Convert(
            await gemini.GenerateJsonAsync(GeminiReader.RaftForm, photo, mimeType, RaftFormPrompt.Text, RaftFormPrompt.Schema, cancellationToken),
            json => json.Deserialize<FormAnswer>(GeminiJsonShapes.Options) is { } form
                ? new RaftFormReading(form.Code, form.Size, form.LaneCount, form.Location, form.Notes, form.Latitude, form.Longitude)
                : null);

    private sealed record FormAnswer(string? Code, string? Size, string? LaneCount, string? Location, string? Notes, string? Latitude, string? Longitude);
}
