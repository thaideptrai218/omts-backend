using System.Text.RegularExpressions;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.Notifications;

/// <summary>What a module notifies: a registered type, an English title and the SRS message of the content.</summary>
/// <param name="Type">A code of <see cref="NotificationTypes"/>.</param>
/// <param name="Message">Its code and placeholder values are stored with the English text, so clients can show it in vi or en.</param>
/// <param name="Record">The record the notification opens (ACCOUNT-08 step 4), if any.</param>
public sealed record NotificationContent(string Type, string Title, UserMessage Message, RelatedRecord? Record = null);

/// <summary>The related record of a notification: a type of <see cref="RelatedRecordTypes"/>, its id and the code shown for it.</summary>
public sealed record RelatedRecord(string Type, Guid Id, string? Code = null);

/// <summary>
/// The record types of notifications and audit events: the entity name of the record ("FarmingZone"), except for zone
/// licenses. The same name is used in both, and one resolver opens them (<c>IRelatedRecordQuery</c>).
/// </summary>
public static partial class RelatedRecordTypes
{
    public const string WaterMeasurement = "WaterMeasurement";
    public const string FarmingZone = "FarmingZone";

    /// <summary>A zone license (FARM-03), stored in the entity ZoneComplianceDocument.</summary>
    public const string ZoneLicense = "ZoneLicense";

    public const string Account = "Account";

    /// <summary>"FarmingZone" → "Farming zone", "ZoneLicense" → "Zone license": the {object_name} of MSG-ACCOUNT-22.</summary>
    public static string ObjectName(string recordType) => string.Join(' ', PascalWords().Split(recordType)
        .Where(w => w.Length > 0)
        .Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex PascalWords();
}
