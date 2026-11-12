using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Audit;

/// <summary>The Vietnamese names and units of the fields kept in the audit snapshots of the farming records, and the names of the actions and roles shown in a history.</summary>
internal static partial class RecordHistoryLabels
{
    private const string Cage = "lồng";

    private static readonly Dictionary<string, (string Label, string? Unit)> HarvestLot = new()
    {
        ["LotCode"] = ("Mã lô", null),
        ["HarvestedAt"] = ("Thời điểm thu hoạch", null),
        ["HarvestDate"] = ("Ngày thu hoạch", null),
        ["PurchaseRequestId"] = ("Đơn đặt mua", null),
        ["Lanes"] = ("Làn thu hoạch", null),
        ["FinishedLanes"] = ("Làn đã thu xong", null),
        ["CageCount"] = ("Số lồng", Cage),
        ["KgPerCage"] = ("Cân nặng ước tính mỗi lồng", "kg/lồng"),
        ["EstimatedKg"] = ("Sản lượng ước tính", "kg"),
        ["OverPlanReason"] = ("Lý do vượt kế hoạch", null),
        ["Photos"] = ("Ảnh", null),
        ["Status"] = ("Trạng thái", null),
        ["CloseDate"] = ("Ngày chốt lô", null),
        ["LeftoverKg"] = ("Số kg còn lại khi chốt", "kg"),
        ["CloseReason"] = ("Lý do chốt lô", null),
        ["ClosedBySalesInvoiceId"] = ("Hóa đơn chốt lô", null),
        ["ReopenedOn"] = ("Ngày mở lại", null),
        ["Lane"] = ("Làn", null),
        ["IsFinished"] = ("Đã thu xong", null),
    };

    private static readonly Dictionary<string, (string Label, string? Unit)> Seeding = new()
    {
        ["SeedingDate"] = ("Ngày thả giống", null),
        ["SeedLot"] = ("Lô giống", null),
        ["Lanes"] = ("Làn thả giống", null),
        ["Quantity"] = ("Số lượng giống", null),
        ["OverPlanReason"] = ("Lý do vượt kế hoạch", null),
    };

    private static readonly Dictionary<string, (string Label, string? Unit)> Material = new()
    {
        ["Raft"] = ("Bè", null),
        ["UsageDate"] = ("Ngày sử dụng", null),
        ["RepairReason"] = ("Lý do sửa chữa", null),
        ["Item"] = ("Vật tư", null),
        ["Quantity"] = ("Số lượng", null),
    };

    private static readonly Dictionary<string, (string Label, string? Unit)> Log = new()
    {
        ["VisitDate"] = ("Ngày ghi nhật ký", null),
        ["Notes"] = ("Nội dung nhật ký", null),
        ["Photos"] = ("Ảnh", null),
    };

    private static readonly Dictionary<string, (string Label, string? Unit)> Cycle = new()
    {
        ["StartDate"] = ("Ngày bắt đầu vụ", null),
        ["ExpectedSeedQuantity"] = ("Số lượng giống dự kiến", null),
        ["ExpectedHarvestDate"] = ("Ngày thu hoạch dự kiến", null),
        ["TargetYieldKg"] = ("Sản lượng mục tiêu", "kg"),
        ["CloseDate"] = ("Ngày chốt vụ", null),
        ["Assessment"] = ("Đánh giá khi chốt vụ", null),
        ["ClosedEarly"] = ("Chốt sớm", null),
        ["EarlyReason"] = ("Lý do chốt sớm", null),
        ["Status"] = ("Trạng thái", null),
        ["SeedingCompletedOn"] = ("Ngày hoàn tất thả giống", null),
    };

    private static readonly Dictionary<string, (string Label, string? Unit)> Nursery = new()
    {
        ["Status"] = ("Trạng thái", null),
        ["StartDate"] = ("Ngày bắt đầu ươm", null),
        ["SeedLot"] = ("Lô giống", null),
        ["Quantity"] = ("Số mảnh vào ươm", "mảnh"),
        ["Note"] = ("Ghi chú", null),
        ["Survivors"] = ("Số mảnh sống sót (ước tính)", "mảnh"),
        ["CompletedOn"] = ("Ngày hoàn tất ươm", null),
        ["Allocations"] = ("Phân bổ cho bè", null),
        ["Photos"] = ("Ảnh", null),
    };

    private static readonly Dictionary<string, string> Actions = new()
    {
        ["SurvivorsCorrected"] = "Sửa số sống sót",
        ["AllocationsChanged"] = "Chuyển phần chia giữa các bè",
        ["Completed"] = "Hoàn tất ươm",
        ["Created"] = "Tạo mới",
        ["Edited"] = "Chỉnh sửa",
        ["TraceCorrected"] = "Hiệu chỉnh truy xuất",
        ["Removed"] = "Xóa",
        ["LaneFinished"] = "Kết thúc thu hoạch làn",
        ["HarvestLanesFinished"] = "Hoàn tất thu hoạch nhiều làn",
        ["Started"] = "Bắt đầu vụ",
        ["SeedingCompleted"] = "Hoàn tất thả giống",
        ["Closed"] = "Chốt",
        ["Cancelled"] = "Hủy",
        ["Reopened"] = "Mở lại",
        ["Published"] = "Công bố truy xuất",
        ["Republished"] = "Công bố lại truy xuất",
        ["Withdrawn"] = "Rút công bố truy xuất",
    };

    private static readonly Dictionary<string, string> Roles = new()
    {
        ["CooperativeOwner"] = "Chủ hợp tác xã",
        ["ZONE_MANAGER"] = "Quản lý vùng",
        ["ACCOUNTANT"] = "Kế toán",
        ["Staff"] = "Nhân viên",
        ["Employee"] = "Nhân viên",
        ["Trader"] = "Thương nhân",
    };

    /// <summary>The label and unit of a field of a record type; an unknown field is shown by its name split into words.</summary>
    public static (string Label, string? Unit) Field(string recordType, string field)
    {
        var table = recordType switch
        {
            "HarvestLot" => HarvestLot,
            "SeedingRecord" => Seeding,
            "MaterialUsage" => Material,
            "CultivationLog" => Log,
            "CultivationCycle" => Cycle,
            "NurseryBatch" => Nursery,
            _ => null,
        };
        return table is not null && table.TryGetValue(field, out var known) ? known : (Words().Replace(field, " $1").Trim(), null);
    }

    /// <summary>The action without its record type, with Corrected and Updated both called Edited.</summary>
    public static string ActionOf(string action)
    {
        var name = action[(action.LastIndexOf('.') + 1)..];
        return name is "Corrected" or "Updated" ? "Edited" : name;
    }

    public static string ActionLabel(string action) => Actions.GetValueOrDefault(action, action);

    public static string? RoleLabel(string? roleCode) => roleCode is null ? null : Roles.GetValueOrDefault(roleCode, roleCode);

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex Words();
}
