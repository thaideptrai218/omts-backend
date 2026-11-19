namespace OMTS.Application.Features.Dashboard;

/// <summary>The kinds of pending task of the operations dashboard (FARM-06): what the client opens with the id of the record.</summary>
public static class PendingTaskKinds
{
    /// <summary>A purchase request Awaiting response (SALE-03).</summary>
    public const string PurchaseRequest = "PurchaseRequest";

    /// <summary>A quality claim Awaiting response (CLAIM-02).</summary>
    public const string QualityClaim = "QualityClaim";

    /// <summary>A payroll Awaiting finalization (LABOR-13).</summary>
    public const string Payroll = "Payroll";

    /// <summary>An employee request (leave, a missed check-in or check-out) Awaiting review by the Owner (LABOR-05).</summary>
    public const string EmployeeRequest = "EmployeeRequest";
}

/// <summary>Daily revenue (BR-FARM-10): the invoices of the period that are not Cancelled, at their latest values. <c>From</c>/<c>To</c> are the business dates actually used.</summary>
public sealed record DashboardRevenueTile(DateOnly From, DateOnly To, decimal Total, int InvoiceCount);

/// <summary>Cooperative-wide customer debt (BR-DEBT-02): <c>Receivables</c> is the total of the debts above zero, <c>OwedBack</c> the money owed back to customers, positive and shown apart.</summary>
public sealed record DashboardReceivablesTile(decimal Receivables, decimal OwedBack);

/// <summary>Harvest lots for sale now, Open or Reopened (BR-HARVEST-04); the client lists them with <c>GET api/harvest-lots/for-sale?zoneId=</c>.</summary>
public sealed record DashboardOpenHarvestLotsTile(int Count);

/// <summary>A zone with a water alert now (BR-WATER-04), to open its water screen.</summary>
public sealed record DashboardAlertZone(Guid ZoneId, string Code, string Name, DateTimeOffset MeasuredAt);

/// <summary>The number of zones with a water alert: a zone counts once, however many values are outside.</summary>
public sealed record DashboardWaterAlertsTile(int Count, IReadOnlyList<DashboardAlertZone> Zones);

/// <summary>
/// One record that waits for the Owner: <c>Id</c> opens it, <c>Code</c> is null for a record with no code. <c>Label</c> names it on the list without opening it: the customer, cages,
/// product and pickup date of a purchase request; the customer and the invoice of a quality claim; the period of a payroll; the employee, the kind and the day or days of an employee request (dates dd/MM/yyyy).
/// </summary>
public sealed record DashboardPendingTask(Guid Id, string? Code, string Label, DateTimeOffset Since);

/// <summary>One <see cref="PendingTaskKinds"/> value, how many records wait, and the longest-waiting few (at most <see cref="OperationsDashboardService.PendingTaskSample"/>).</summary>
public sealed record DashboardPendingTaskGroup(string Kind, int Count, IReadOnlyList<DashboardPendingTask> Oldest);

public sealed record DashboardPendingTasksTile(int Total, IReadOnlyList<DashboardPendingTaskGroup> Groups);

/// <summary>
/// The sales result of one business day (KiotViet-style "Kết quả bán hàng hôm nay"): the invoices not Cancelled by invoice date, the sales returns that stand by return date. <c>NetRevenue</c> is
/// <c>Revenue − ReturnValue</c>. Of the zone chosen, or of every zone.
/// </summary>
public sealed record DashboardDayTile(DateOnly Date, int InvoiceCount, decimal Revenue, int ReturnCount, decimal ReturnValue, decimal NetRevenue);

/// <summary>
/// Customers whose debt is due, by the debt limit of the cooperative (the days limit of the customer settings): the invoices have no due date of their own, so "due" means the oldest unpaid
/// invoice is older than <c>MaxDays</c> (<c>OverdueCount</c>, also customers over the amount limit) or reaches it within 7 days (<c>DueSoonCount</c>). Both are 0 and <c>PolicyEnabled</c>
/// false while the debt warning is off; <c>DebtorCount</c> (customers owing more than 0) is always given.
/// </summary>
public sealed record DashboardDueDebtsTile(int DebtorCount, int OverdueCount, int DueSoonCount, bool PolicyEnabled, int? MaxDays);

/// <summary>
/// FARM-06: every tile worked out now (<c>ComputedAt</c>); the filters are echoed (<c>ZoneId</c>, the period in <c>DailyRevenue</c>). <c>Today</c>/<c>Yesterday</c> and <c>DueDebts</c> were added
/// for the KiotViet-style overview. An Accountant reads the same tiles, but <c>PendingTasks</c> (the Owner's decisions) is empty for them.
/// </summary>
public sealed record OperationsDashboard(
    DateTimeOffset ComputedAt,
    Guid? ZoneId,
    DashboardRevenueTile DailyRevenue,
    DashboardReceivablesTile Receivables,
    DashboardOpenHarvestLotsTile OpenHarvestLots,
    DashboardWaterAlertsTile WaterAlerts,
    DashboardPendingTasksTile PendingTasks,
    DashboardDayTile Today,
    DashboardDayTile Yesterday,
    DashboardDueDebtsTile DueDebts);
