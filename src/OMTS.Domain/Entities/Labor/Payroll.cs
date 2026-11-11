using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// A payroll sheet (LABOR-12, LABOR-13, BR-LABOR-10): the pay of the employees for a period, one <see cref="PayrollLine"/> (payslip)
/// per employee. It is Provisional (the catalog's Draft) while its lines follow the current records, Finalized once the Owner has
/// finalized it, or Cancelled. Finalizing locks the records of the period (BR-LABOR-13, see PayPeriodLock). Its row version serializes
/// those actions against a dated change. The name of the sheet comes from its period.
/// </summary>
public class Payroll : AuditableEntity, IHasStatus
{
    /// <summary>BL000001: one sequence per cooperative; empty only on a header older than the sheets.</summary>
    public string Code { get; set; } = string.Empty;

    public PayrollPeriodType PeriodType { get; set; } = PayrollPeriodType.Custom;
    public PayrollScope Scope { get; set; } = PayrollScope.All;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>Provisional (Draft) or Finalized; Creating and Cancelled are the web contract's other two. Awaiting finalization and Sent back are SRS v8 states no endpoint reaches.</summary>
    public int StatusId { get; set; } = StatusIds.Payroll.Draft;
    public Status Status { get; set; } = null!;

    /// <summary>The month (yyyy-MM) the wages count in as a cost; set when the sheet is finalized.</summary>
    public string? ExpenseMonth { get; set; }

    public string? Note { get; set; }

    /// <summary>Created by the daily check of automatic payroll (GFR-07); otherwise by the user in CreatedBy.</summary>
    public bool CreatedBySystem { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public Guid? SentByAccountId { get; set; }

    public string? SendBackNote { get; set; }
    public DateTimeOffset? SentBackAt { get; set; }
    public Guid? SentBackByAccountId { get; set; }

    public DateTimeOffset? FinalizedAt { get; set; }
    public Guid? FinalizedByAccountId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }

    public ICollection<PayrollLine> Lines { get; set; } = [];

    /// <summary>The employees chosen when the scope is Selected, so a reload keeps them even when one has no hours.</summary>
    public ICollection<PayrollSelectedEmployee> SelectedEmployees { get; set; } = [];

    /// <summary>The records of the period can no longer change (BR-LABOR-13).</summary>
    public bool IsLocked => StatusId is StatusIds.Payroll.AwaitingFinalization or StatusIds.Payroll.Finalized;

    /// <summary>Provisional (or Creating, or a state of SRS v8 no endpoint reaches): lines may be reloaded and the sheet finalized or cancelled.</summary>
    public bool IsOpen => StatusId is StatusIds.Payroll.Draft or StatusIds.Payroll.Creating or StatusIds.Payroll.AwaitingFinalization or StatusIds.Payroll.SentBack;
}

public class PayrollSelectedEmployee
{
    public Guid PayrollId { get; set; }
    public Payroll Payroll { get; set; } = null!;
    public Guid EmployeeId { get; set; }
}
