namespace OMTS.Domain.Enums;

/// <summary>What a work-day plan is for. The kind decides the job type (and so the shift rate) of the people on it, and the place it needs.</summary>
public enum WorkDayPlanKind
{
    /// <summary>Thu hoạch: one raft, made only from a line of a harvest plan.</summary>
    Harvest,

    /// <summary>Ươm giống: one zone, optionally with the running nursery batch for a care day.</summary>
    Nursery,

    /// <summary>Thả giống: one raft.</summary>
    Seeding,
}

/// <summary>
/// Where a work-day plan stands. <c>Staffing</c> and <c>Ready</c> swap as people are confirmed or taken off until the work starts; <c>InProgress</c> starts with the first check-in
/// on the plan and <c>Completed</c> ends it; <c>Cancelled</c> can follow any state before Completed.
/// </summary>
public enum WorkDayPlanStatus
{
    /// <summary>Chưa đủ người: fewer people than needed. The plan cannot run.</summary>
    Staffing,

    /// <summary>Đủ người: confirmed members plus people hired outside reach the people needed.</summary>
    Ready,

    /// <summary>Đang làm.</summary>
    InProgress,

    /// <summary>Hoàn thành.</summary>
    Completed,

    /// <summary>Đã hủy.</summary>
    Cancelled,
}

/// <summary>The answer of one person invited to a work-day plan.</summary>
public enum WorkDayMemberState
{
    /// <summary>Đã mời: waiting for the answer until the deadline.</summary>
    Invited,

    /// <summary>Đã nhận: the person accepted.</summary>
    Accepted,

    /// <summary>Xác nhận thay: the Zone Manager or the Owner confirmed for the person, with a reason.</summary>
    ConfirmedOnBehalf,

    /// <summary>Từ chối.</summary>
    Declined,

    /// <summary>Hết hạn: no answer before the deadline.</summary>
    Expired,

    /// <summary>Không thành công: still pending when the plan became Ready, or accepted too late.</summary>
    Unsuccessful,
}
