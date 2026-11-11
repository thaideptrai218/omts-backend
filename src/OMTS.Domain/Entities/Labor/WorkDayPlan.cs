using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Domain.Entities.Labor;

/// <summary>Kế hoạch ngày làm: Chủ HTX tự phụ trách hoặc giao quản lý xác nhận trước khi phân công.</summary>
public class WorkDayPlan : AuditableEntity
{
    public Guid CreatorAccountId { get; set; }
    public Account CreatorAccount { get; set; } = null!;
    public Guid ResponsibleAccountId { get; set; }
    public Account ResponsibleAccount { get; set; } = null!;
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;
    public Guid JobTypeId { get; set; }
    public JobType JobType { get; set; } = null!;
    public DateOnly WorkDate { get; set; }
    public Guid? PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    public Guid ClientOperationId { get; set; }
    public string? Note { get; set; }
    public bool? Approved { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public string? ResponseReason { get; set; }
    public ICollection<WorkAssignment> Assignments { get; set; } = [];
}
