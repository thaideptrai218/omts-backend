using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Sales;

/// <summary>Lời mời xác nhận ca, tách khỏi chấm công thực tế. Một người chỉ cam kết một đơn mỗi ngày.</summary>
public class PurchaseShiftInvitation : AuditableEntity
{
    public Guid PurchaseRequestId { get; set; }
    public PurchaseRequest PurchaseRequest { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public bool? Approved { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    // Chỉ giữ ngày khi đã đồng ý và đơn chưa bị hủy/từ chối; unique index chống nhận trùng đồng thời.
    public DateOnly? ReservedWorkDate { get; set; }
}
