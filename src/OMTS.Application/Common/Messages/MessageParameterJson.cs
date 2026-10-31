using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace OMTS.Application.Common.Messages;

/// <summary>
/// The JSON of <see cref="UserMessage.Parameters"/> as notifications store and push it: a JSON object with one atomic
/// value per parameter. Dates are ISO dates ("2026-10-01"), times ISO 8601 with the Vietnam offset, enums by name, and
/// the properties of structured values in snake_case (e.g. <c>values_outside[].direction</c> = "High").
/// </summary>
public static class MessageParameterJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
        // Vietnamese names stay readable in the stored JSON.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>The JSON object, or null when the message has no parameters.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, object?>? parameters) =>
        parameters is { Count: > 0 } ? JsonSerializer.Serialize(parameters, Options) : null;
}
