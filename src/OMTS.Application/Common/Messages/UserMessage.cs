using System.Text.Json.Serialization;

namespace OMTS.Application.Common.Messages;

/// <summary>
/// A message from the SRS message list: <see cref="Code"/> (MSG-GEN-nn, MSG-&lt;feature&gt;-nn) is what clients map to
/// the localized text; <see cref="Text"/> is the English SRS wording with its placeholders filled in.
/// <see cref="LegacyCode"/> is the code the same situation had before the SRS 27/10/2026 catalogue (MSGnn or a
/// technical code), sent for one release so that clients can switch on their own schedule; null when the code did
/// not change.
/// </summary>
public sealed record UserMessage(
    string Code,
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LegacyCode = null)
{
    /// <summary>
    /// The values of the SRS placeholders in <see cref="Text"/> by placeholder name (e.g. <c>zone_name</c>), for the
    /// messages a client shows later in its own language (notification contents); null for the other messages.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}
