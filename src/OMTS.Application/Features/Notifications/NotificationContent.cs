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

    /// <summary>A certification of the cooperative (FARM-01.1).</summary>
    public const string CooperativeCertification = "CooperativeCertification";

    public const string Account = "Account";

    /// <summary>A cultivation log; the displayed code is its raft code.</summary>
    public const string CultivationLog = "CultivationLog";
    public const string PurchaseRequest = "PurchaseRequest";

    /// <summary>A cultivation cycle (CYCLE-02); it has no code of its own, so the code shown is its raft's.</summary>
    public const string CultivationCycle = "CultivationCycle";

    /// <summary>A harvest lot (HARVEST-01); the code shown is its lot code.</summary>
    public const string HarvestLot = "HarvestLot";

    /// <summary>A purchase receipt (PURCHASE-01, PURCHASE-06); it has no zone.</summary>
    public const string PurchaseReceipt = "PurchaseReceipt";

    /// <summary>A supplier payment or money paid back (PURCHASE-05); it has no zone and is never removed, only cancelled.</summary>
    public const string SupplierPayment = "SupplierPayment";

    /// <summary>A sales invoice (SALE-05); its zone is the zone of the invoice, and the code shown is HD000123.</summary>
    public const string SalesInvoice = "SalesInvoice";

    /// <summary>A customer payment or money paid back (DEBT-02); it has no zone and is never removed, only cancelled; the code shown is PT000123.</summary>
    public const string CustomerPayment = "CustomerPayment";

    /// <summary>
    /// "FarmingZone" → "Farming zone", "ZoneLicense" → "Zone license": the {object_name} of MSG-ACCOUNT-22. A certification
    /// of the cooperative reads "Certification", not the words of its entity.
    /// </summary>
    public static string ObjectName(string recordType) => recordType == CooperativeCertification
        ? "Certification"
        : string.Join(' ', PascalWords().Split(recordType)
            .Where(w => w.Length > 0)
            .Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex PascalWords();
}
