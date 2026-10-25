using System.Text.Json;
using System.Text.Json.Serialization;
using OMTS.Application.Common;

namespace OMTS.Api.Common;

/// <summary>
/// Reads every JSON <see cref="DateTimeOffset"/> (and <c>DateTimeOffset?</c>) of a request as ISO 8601 by the business
/// time rule: a value with "Z" or an explicit offset keeps it, a value without one is Vietnam time
/// (<see cref="BusinessClock.FromLocal"/>). System.Text.Json alone would give it the server's offset, which is UTC in
/// Docker. Writing is unchanged: the value with its own offset.
/// </summary>
internal sealed class BusinessTimeJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            // The reader's own ISO 8601 parser tells whether an offset was sent: none gives an Unspecified kind.
            if (reader.TryGetDateTime(out var parsed) && parsed.Kind == DateTimeKind.Unspecified)
            {
                return BusinessClock.FromLocal(parsed);
            }

            if (reader.TryGetDateTimeOffset(out var instant))
            {
                return instant;
            }
        }

        // Answered as 400 MSG01 naming the field (InvalidModelStateResponse).
        throw new JsonException("The value is not an ISO 8601 date and time.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
