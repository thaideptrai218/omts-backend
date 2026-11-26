using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr.Gemini.Prompts;

namespace OMTS.Infrastructure.Ocr.Gemini;

/// <summary>
/// OCR Service adapter (WATER-01) on Gemini: sends the whole meter photo (the server has no image library, so a crop is accepted
/// and ignored) with the spike prompt and turns the answer into suggestions. The model only copies the digits it sees; the
/// server parses them, and sets the confidence: 0.5 for a value the model flagged uncertain, else 0.95 (the user sees "uncertain"
/// below <c>Ocr:UncertainBelow</c>). Ranges are checked later by <c>WaterMeterRecognizer</c>. A failure is a failed result, never an exception.
/// </summary>
internal sealed partial class GeminiMeterOcrService(IGeminiVisionClient gemini) : IOcrService
{
    public const double UncertainConfidence = 0.5;
    public const double CertainConfidence = 0.95;

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss",
        "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "dd-MM-yyyy HH:mm", "dd-MM-yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss",
    ];

    public async Task<OcrResult> RecognizeAsync(Stream photo, string contentType, OcrCrop? crop, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        await photo.CopyToAsync(copy, cancellationToken);
        var answer = await gemini.GenerateJsonAsync(GeminiReader.Meter, copy.ToArray(), contentType, MeterPrompt.Text, MeterPrompt.Schema, cancellationToken);
        if (!answer.Ok || answer.Json is not { } json)
        {
            return OcrResult.Failed(answer.ErrorCode ?? OcrErrorCodes.BadResponse);
        }

        return OcrResult.Read(Number(json, "ph"), Number(json, "salinity"), Number(json, "temperature"), MeasuredAt(json));
    }

    private static OcrReading<decimal>? Number(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Object
            || !field.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String
            || ParseDecimal(value.GetString()) is not { } number)
        {
            return null;
        }

        var uncertain = field.TryGetProperty("uncertain", out var flag) && flag.ValueKind != JsonValueKind.False;
        return new OcrReading<decimal>(number, uncertain ? UncertainConfidence : CertainConfidence);
    }

    private static OcrReading<DateTimeOffset>? MeasuredAt(JsonElement json) =>
        json.TryGetProperty("measured_at", out var text) && text.ValueKind == JsonValueKind.String
        && DateTime.TryParseExact(text.GetString()?.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new OcrReading<DateTimeOffset>(new DateTimeOffset(local, BusinessClock.UtcOffset), CertainConfidence)
            : null;

    /// <summary>"7.8" or "7,8": one to four digits, an optional decimal part of up to three. Anything else is not read.</summary>
    internal static decimal? ParseDecimal(string? text)
    {
        var trimmed = text?.Trim().Replace(',', '.');
        return trimmed is not null && Plain().IsMatch(trimmed) ? decimal.Parse(trimmed, CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"^[0-9]{1,4}(\.[0-9]{1,3})?$")]
    private static partial Regex Plain();
}
