using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>The kind of a use issue ("Loại xuất"): the system types are seeded and never change; the others are made by the Owner and hidden instead of deleted once an issue has them.</summary>
public class InputIssueType : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsSystem { get; set; }

    /// <summary>False once the type is hidden: not offered any more, still named on the issues that have it.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A document that takes input items out of the stock: a use in a zone (XD000001) or a disposal (XH000001). A Draft holds no quantity and is removed, never cancelled; a Completed
/// issue writes its quantities on the movement ledger and stays editable (its rows are undone and written again); a Cancelled one counts nowhere.
/// </summary>
public class InputIssue : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;
    public InputIssueKind Kind { get; set; }

    /// <summary>When the items left, stored at UTC+7.</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>The zone the items were used in; required for a use, empty on a disposal.</summary>
    public Guid? ZoneId { get; set; }
    public FarmingZone? Zone { get; set; }

    /// <summary>The raft the items were used for; required for a use, within the zone, empty on a disposal. An issue belongs to one zone and one raft.</summary>
    public Guid? RaftId { get; set; }
    public Raft? Raft { get; set; }

    public Guid? IssueTypeId { get; set; }
    public InputIssueType? IssueType { get; set; }

    /// <summary>The employee who took the items, working in the zone.</summary>
    public Guid? ReceiverEmployeeId { get; set; }
    public Employee? Receiver { get; set; }

    /// <summary>Required on a disposal.</summary>
    public string? Reason { get; set; }
    public string? Notes { get; set; }

    public int StatusId { get; set; } = StatusIds.InputIssue.Draft;
    public Status Status { get; set; } = null!;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>The farming record that posted this issue and owns it; <see cref="InputIssueSource.None"/> for a manual issue.</summary>
    public InputIssueSource SourceKind { get; set; }

    /// <summary>The seeding session or material usage of <see cref="SourceKind"/>: the link the other way round, with its cycle (null for a usage outside any cycle).</summary>
    public Guid? SourceRecordId { get; set; }
    public Guid? CycleId { get; set; }

    public ICollection<InputIssueLine> Lines { get; set; } = [];
}

/// <summary>One item on an issue. Quantity is in BASE units (<see cref="EnteredQuantity"/> × <see cref="UnitFactor"/>); the cost is a snapshot taken when the issue was saved or completed.</summary>
public class InputIssueLine : Entity
{
    public Guid IssueId { get; set; }
    public InputIssue Issue { get; set; } = null!;
    public int LineNumber { get; set; }
    public Guid ItemId { get; set; }
    public FarmingInputItem Item { get; set; } = null!;

    public Guid? ItemUnitId { get; set; }
    public InputItemUnit? ItemUnit { get; set; }

    /// <summary>Base units in one unit of the line, as it was when saved.</summary>
    public decimal UnitFactor { get; set; } = 1m;

    /// <summary>The name of the unit of the line, kept for history.</summary>
    public string Unit { get; set; } = string.Empty;
    public decimal EnteredQuantity { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>The seed lot (a line of a Completed receipt) a seed quantity is taken from; empty draws from the quantity of the item that has no lot.</summary>
    public Guid? SeedLotLineId { get; set; }
    public PurchaseReceiptLine? SeedLotLine { get; set; }

    /// <summary>The cost of one UNIT of the line: the cost of the item per base unit × the factor.</summary>
    public decimal UnitCost { get; set; }

    /// <summary>Entered quantity × unit cost, half up to the dong.</summary>
    public decimal Value { get; set; }
    public string? Note { get; set; }
}
