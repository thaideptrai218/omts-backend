using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>The field checks of a usage record (BR-CYCLE-03; MSG-GEN-01 names the fields), and its audit rows (GBR-02).</summary>
internal static class MaterialUsageValues
{
    public const string RecordType = nameof(MaterialUsage);
    public const string Created = "MaterialUsage.Created";
    public const string Updated = "MaterialUsage.Updated";

    public const string DateField = "Repair date";
    public const string RepairReasonField = "Repair reason";
    public const string ItemsField = "Items";
    public const string ItemField = "Item";
    public const string QuantityField = "Quantity";
    public const string RaftField = "Raft";
    public const string ReasonField = "Reason";

    private const int MaxReasonLength = 1000;

    // Well inside decimal(18,3), as on a receipt line.
    private const decimal MaxQuantity = 1_000_000_000m;

    public static string LineItemField(int index) => $"Line {index + 1} item";

    public static string LineQuantityField(int index) => $"Line {index + 1} quantity";

    public static void CheckDate(FieldCheck check, DateOnly? date, DateOnly today) => check.Check(date <= today, DateField);

    public static string CheckRepairReason(FieldCheck check, string? reason) => check.Required(reason, RepairReasonField, MaterialUsage.MaxRepairReasonLength);

    /// <summary>The reason for an edit, kept in the audit log.</summary>
    public static string CheckReason(FieldCheck check, string? reason) => check.Required(reason, ReasonField, MaxReasonLength);

    /// <summary>Above 0, and a value the column stores as it is: nothing is rounded without a word.</summary>
    public static void CheckQuantity(FieldCheck check, decimal? quantity, string field) =>
        check.Check(quantity is > 0 and <= MaxQuantity && decimal.Round(quantity.Value, 3) == quantity.Value, field);

    /// <summary>An item of the catalogue, in the group Material. Whether it is In Use is asked after the fields, as that answer names the item.</summary>
    public static bool IsMaterial(FarmingInputItem? item) => item?.Group == MaterialUsage.ItemGroup;

    public static bool IsDiscontinued(FarmingInputItem item) => item.StatusId != StatusIds.Usage.InUse;

    /// <summary>The audit values: codes rather than ids, as a reader of the log knows them.</summary>
    public static object Snapshot(MaterialUsage usage, Raft raft, FarmingInputItem item) => new
    {
        Raft = raft.Code,
        usage.UsageDate,
        usage.RepairReason,
        Item = item.Code,
        usage.Quantity,
    };

    public static AuditEntry Entry(string action, MaterialUsage usage, Raft raft) => new(action, RecordType)
    {
        RecordId = usage.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId,
    };
}
