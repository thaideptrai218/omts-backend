using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.Notifications;

/// <summary>What a module notifies: a registered type, an English title and the SRS message of the content.</summary>
/// <param name="Type">A code of <see cref="NotificationTypes"/>.</param>
/// <param name="Message">Its code and placeholder values are stored with the English text, so clients can show it in vi or en.</param>
/// <param name="Record">The record the notification opens (ACCOUNT-08 step 4), if any.</param>
public sealed record NotificationContent(string Type, string Title, UserMessage Message, RelatedRecord? Record = null);

/// <summary>The related record of a notification: a type of <see cref="RelatedRecordTypes"/>, its id and the code shown for it.</summary>
public sealed record RelatedRecord(string Type, Guid Id, string? Code = null);

/// <summary>Record types notifications point to, with their names in MSG-ACCOUNT-22 (<c>{object_name}</c>).</summary>
public static class RelatedRecordTypes
{
    public const string WaterMeasurement = "WaterMeasurement";
    public const string FarmingZone = "FarmingZone";

    /// <summary>A zone license (FARM-03); the name of the old entity is kept for the stored rows.</summary>
    public const string ZoneLicense = "ZoneComplianceDocument";

    public const string Account = "Account";

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [WaterMeasurement] = "Water measurement",
        [FarmingZone] = "Farming zone",
        [ZoneLicense] = "Zone license",
        [Account] = "Account",
    };
}
