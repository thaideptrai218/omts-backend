using System.Text.Json.Serialization;

namespace OMTS.Infrastructure.Messaging.Fakes;

/// <summary>A message a fake sender "delivered", kept in memory for the dev-only inbox endpoints and tests.</summary>
/// <param name="Devices">Push only: the device tokens a real provider would deliver to (empty: none).</param>
public sealed record FakeMessage(
    string Id,
    DateTimeOffset SentAt,
    string Recipient,
    string? Subject,
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Devices = null);
