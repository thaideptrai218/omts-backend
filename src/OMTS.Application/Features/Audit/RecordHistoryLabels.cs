namespace OMTS.Application.Features.Audit;

/// <summary>The Vietnamese names and units of the fields kept in the audit snapshots of the farming records, and the names of the actions and roles shown in a history.</summary>
internal static class RecordHistoryLabels
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
        ["LanePercents"] = ("Phần trăm đã thu từng làn", null),
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
        ["WeighedKg"] = ("Số kg cân thực tế", "kg"),
        ["WeighedAt"] = ("Thời điểm cân", null),
        ["WeighedOn"] = ("Ngày cân", null),
        ["WeighedLines"] = ("Kg cân từng sản phẩm", null),
        ["Lane"] = ("Làn", null),
        ["IsFinished"] = ("Đã thu xong", null),
        ["ProductLines"] = ("Sản phẩm thu hoạch", null),
        ["Crew"] = ("Nhân viên tham gia", null),
        ["OriginState"] = ("Truy xuất nguồn gốc", null),
        ["PublishedAt"] = ("Thời điểm công khai", null),
        ["WithdrawnAt"] = ("Thời điểm rút công khai", null),
        ["SalesInvoiceCode"] = ("Mã hóa đơn bán", null),
        ["NotifiedTraders"] = ("Thương nhân đã được báo", null),
        ["WalkInInvoiceCodes"] = ("Hóa đơn khách lẻ", null),
        ["WeighingAgreedKg"] = ("Số kg thương nhân xác nhận", "kg"),
        ["WeighingAgreedFor"] = ("Thời điểm cân được xác nhận", null),
        ["TraderCages"] = ("Số lồng thương nhân cân", Cage),
        ["TraderCagesKg"] = ("Số kg thương nhân cân", "kg"),
        ["OwnerApprovalConfirmed"] = ("Chủ hợp tác xã đã đồng ý", null),
    };

    private static readonly Dictionary<string, (string Label, string? Unit)> Seeding = new()
    {
        ["SeedingDate"] = ("Ngày thả giống", null),
        ["SeedLot"] = ("Lô giống", null),
        ["Lanes"] = ("Làn thả giống", null),
        ["LanePercents"] = ("Phần trăm đã thả từng làn", null),
        ["Quantity"] = ("Số lượng giống", null),
        ["Photos"] = ("Ảnh", null),
        ["OverPlanReason"] = ("Lý do vượt kế hoạch", null),
        ["ResponsibleAccountId"] = ("Người phụ trách", null),
        ["Crew"] = ("Nhân viên tham gia", null),
        ["OwnerApprovalConfirmed"] = ("Chủ hợp tác xã đã đồng ý", null),
    };

    private static readonly Dictionary<string, (string Label, string? Unit)> LaneProgress = new()
    {
        ["SeededPercent"] = ("Phần trăm đã thả", "%"),
        ["HarvestedPercent"] = ("Phần trăm đã thu", "%"),
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
        ["LogType"] = ("Loại nhật ký", null),
        ["Notes"] = ("Nội dung nhật ký", null),
        ["Photos"] = ("Ảnh", null),
        ["OwnerApprovalConfirmed"] = ("Chủ hợp tác xã đã đồng ý", null),
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
        ["Note"] = ("Ghi chú", null),
        ["NurseryBatch"] = ("Đợt ươm", null),
        ["Photos"] = ("Ảnh kế hoạch", null),
        ["Product"] = ("Sản phẩm", null),
        ["SeededCount"] = ("Số giống đã thả", null),
        ["LaneCount"] = ("Số làn đã thả", null),
        ["OwnerApprovalConfirmed"] = ("Chủ hợp tác xã đã đồng ý", null),
    };

    /// <summary>The figures of the close day kept in the close event of a cycle under <c>Summary</c>; each is shown as a field of its own.</summary>
    internal static readonly Dictionary<string, (string Label, string? Unit)> CycleSummary = new()
    {
        ["SeedHung"] = ("Số giống đã thả", null),
        ["KgHarvested"] = ("Sản lượng đã thu (ước tính)", "kg"),
        ["LastHarvestDate"] = ("Ngày thu hoạch cuối", null),
        ["DaysOfFarming"] = ("Số ngày nuôi", "ngày"),
        ["ExpectedSeedQuantity"] = ("Số lượng giống dự kiến", null),
        ["TargetYieldKg"] = ("Sản lượng mục tiêu", "kg"),
        ["EarlyReason"] = ("Lý do chốt sớm", null),
        ["NotHarvestedLanes"] = ("Làn chưa thu hoạch", null),
    };

    private static readonly Dictionary<string, (string Label, string? Unit)> Nursery = new()
    {
        ["Status"] = ("Trạng thái", null),
        ["StartDate"] = ("Ngày bắt đầu ươm", null),
        ["SeedLot"] = ("Lô giống", null),
        ["Quantity"] = ("Số mảnh vào ươm", "mảnh"),
        ["Note"] = ("Ghi chú", null),
        ["Survivors"] = ("Số mảnh sống sót (ước tính)", "mảnh"),
        ["CompletedOn"] = ("Ngày ươm xong", null),
        ["ClosedOn"] = ("Ngày đóng đợt ươm", null),
        ["CloseLossPieces"] = ("Hao hụt khi đóng đợt (ước tính)", "mảnh"),
        ["Allocations"] = ("Phân bổ cho bè", null),
        ["Photos"] = ("Ảnh", null),
        ["ResponsibleAccountId"] = ("Người phụ trách", null),
        ["Crew"] = ("Nhân viên tham gia", null),
    };


    /// <summary>What is said of an action nobody named: never the English code.</summary>
    public const string UnknownAction = "Thay đổi";

    /// <summary>Said of a field nobody named; the fields of every record type are named (a test checks it), so this only shows for a field of a later version.</summary>
    public const string UnknownField = "Thông tin khác";

    private static readonly Dictionary<string, string> Actions = new()
    {
        ["SurvivorsCorrected"] = "Sửa số sống sót",
        ["AllocationsChanged"] = "Chuyển phần chia giữa các bè",
        ["Completed"] = "Ươm xong",
        ["Created"] = "Tạo mới",
        ["Recorded"] = "Ghi nhận",
        ["Edited"] = "Chỉnh sửa",
        ["TraceCorrected"] = "Hiệu chỉnh truy xuất",
        ["Removed"] = "Xóa",
        ["Deleted"] = "Xóa",
        ["ProgressChanged"] = "Chỉnh phần trăm tiến độ làn",
        ["LaneFinished"] = "Kết thúc thu hoạch làn",
        ["LanesFinished"] = "Hoàn tất thu hoạch nhiều làn",
        ["HarvestLanesFinished"] = "Hoàn tất thu hoạch nhiều làn",
        ["Started"] = "Bắt đầu vụ",
        ["SeedingCompleted"] = "Hoàn tất thả giống",
        ["Closed"] = "Chốt",
        ["Cancelled"] = "Hủy",
        ["Voided"] = "Hủy bỏ",
        ["Reopened"] = "Mở lại",
        ["Weighed"] = "Cân lô",
        ["Reweighed"] = "Cân lại lô",
        ["WeighingAgreed"] = "Thương nhân xác nhận cân",
        ["Published"] = "Công khai truy xuất",
        ["Republished"] = "Công khai lại truy xuất",
        ["Withdrawn"] = "Rút công khai truy xuất",
    };

    /// <summary>The same action named for a record type where the plain name reads wrong: a lot is "ended" (Kết thúc lô), a cycle is "closed" (Chốt vụ), a nursery batch "Đóng đợt ươm".</summary>
    private static readonly Dictionary<(string RecordType, string Action), string> ActionsOfRecordType = new()
    {
        [("NurseryBatch", "Closed")] = "Đóng đợt ươm",
        [("NurseryBatch", "Reopened")] = "Mở lại đợt ươm",
        [("NurseryBatch", "Cancelled")] = "Hủy đợt ươm",
        [("HarvestLot", "Closed")] = "Kết thúc lô",
        [("HarvestLot", "Created")] = "Ghi nhận lô thu hoạch",
        [("CultivationCycle", "Closed")] = "Chốt vụ",
        [("CultivationCycle", "Cancelled")] = "Hủy vụ",
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

    private static readonly Dictionary<string, string> CloseReasons = new()
    {
        ["SoldWholeLot"] = "Bán hết lô",
        ["Unsold"] = "Không bán hết",
        ["Spoiled"] = "Hư hỏng",
        ["TimeLimitReached"] = "Quá thời hạn bán",
    };

    private static readonly Dictionary<string, string> OriginStates = new()
    {
        ["NotPublished"] = "Chưa công khai",
        ["Published"] = "Đã công khai",
        ["Withdrawn"] = "Đã rút công khai",
    };

    private static readonly Dictionary<string, string> LogTypes = new()
    {
        ["FEEDING"] = "Cho ăn",
        ["CLEANING"] = "Vệ sinh bè/lồng",
        ["INSPECTION"] = "Kiểm tra",
        ["DISEASE"] = "Phòng/trị bệnh",
        ["REPAIR"] = "Sửa chữa",
        ["OTHER"] = "Khác",
    };

    private static readonly Dictionary<string, string> HarvestLotStatuses = new()
    {
        ["AWAITING_WEIGHING"] = "Chờ cân",
        ["OPEN"] = "Đang mở",
        ["REOPENED"] = "Đã mở lại",
        ["CLOSED"] = "Đã kết thúc",
    };

    private static readonly Dictionary<string, string> CycleStatuses = new()
    {
        ["OPEN"] = "Đang nuôi",
        ["CLOSED"] = "Đã chốt",
    };

    private static readonly Dictionary<string, string> EarlyReasons = new()
    {
        ["NoSeedingSession"] = "Chưa có lần thả giống nào",
        ["LanesNotHarvested"] = "Còn làn chưa thu hoạch",
    };

    /// <summary>Why a closed lot was opened again, as the system writes it in the reason of the event.</summary>
    private static readonly Dictionary<string, string> Reasons = new()
    {
        ["SalesReturnBackForSale"] = "Hàng trả về được bán lại",
        ["SalesInvoiceCancelled"] = "Hóa đơn chốt lô đã bị hủy",
        ["SoldWholeTickRemoved"] = "Bỏ đánh dấu bán hết lô",
        ["ReopenedByHand"] = "Mở lại thủ công",
    };

    /// <summary>The label and unit of a field of a record type; a field nobody named is <see cref="UnknownField"/>.</summary>
    public static (string Label, string? Unit) Field(string recordType, string field)
    {
        var table = TableOf(recordType);
        return table is not null && table.TryGetValue(field, out var known) ? known : (UnknownField, null);
    }

    /// <summary>True when the field of the record type has a Vietnamese label.</summary>
    public static bool HasField(string recordType, string field) => TableOf(recordType)?.ContainsKey(field) == true;

    /// <summary>The label and unit of one of the figures kept under <c>Summary</c> in the close of a cycle; null for one that is not shown.</summary>
    public static (string Label, string? Unit)? SummaryField(string field) => CycleSummary.TryGetValue(field, out var known) ? known : null;

    private static Dictionary<string, (string Label, string? Unit)>? TableOf(string recordType) => recordType switch
    {
        "HarvestLot" => HarvestLot,
        "SeedingRecord" => Seeding,
        "MaterialUsage" => Material,
        "CultivationLog" => Log,
        "CultivationCycle" => Cycle,
        "NurseryBatch" => Nursery,
        "Lane" => LaneProgress,
        _ => null,
    };

    /// <summary>The Vietnamese name of a coded value of a field (a status, a close reason, the state of the origin, a type of log); null when the value is not a code of that field.</summary>
    public static string? Value(string recordType, string field, string code) => field switch
    {
        "Status" when recordType == "HarvestLot" => HarvestLotStatuses.GetValueOrDefault(code),
        "Status" when recordType == "CultivationCycle" => CycleStatuses.GetValueOrDefault(code),
        "CloseReason" => CloseReasons.GetValueOrDefault(code),
        "OriginState" => OriginStates.GetValueOrDefault(code),
        "LogType" => LogTypes.GetValueOrDefault(code),
        "EarlyReason" => EarlyReasons.GetValueOrDefault(code),
        _ => null,
    };

    /// <summary>The reason of an event: a code the system wrote as the reason is told in words, a reason somebody typed stays as it is.</summary>
    public static string? ReasonLabel(string? reason) => reason is null ? null : Reasons.GetValueOrDefault(reason, reason);

    /// <summary>The action without its record type, with Corrected and Updated both called Edited.</summary>
    public static string ActionOf(string action)
    {
        var name = action[(action.LastIndexOf('.') + 1)..];
        return name is "Corrected" or "Updated" ? "Edited" : name;
    }

    /// <summary>The Vietnamese label of an action; one nobody named is <see cref="UnknownAction"/>, never the English code.</summary>
    public static string ActionLabel(string action, string? recordType = null) =>
        recordType is not null && ActionsOfRecordType.TryGetValue((recordType, action), out var ofRecordType) ? ofRecordType
        : Actions.GetValueOrDefault(action, UnknownAction);

    /// <summary>True when the action (without its record type) has a Vietnamese name.</summary>
    internal static bool HasAction(string action) => Actions.ContainsKey(action);

    public static string? RoleLabel(string? roleCode) => roleCode is null ? null : Roles.GetValueOrDefault(roleCode, roleCode);
}
