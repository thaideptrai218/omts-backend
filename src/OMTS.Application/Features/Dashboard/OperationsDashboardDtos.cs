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
/// One record that waits for the Owner: <c>Id</c> opens it, <c>Code</c> is null for a record with no code. <c>Label</c> names it on the list without opening it: the customer, kg,
/// product and pickup date of a purchase request; the customer and the invoice of a quality claim; the period of a payroll (dates dd/MM/yyyy).
/// </summary>
public sealed record DashboardPendingTask(Guid Id, string? Code, string Label, DateTimeOffset Since);

/// <summary>One <see cref="PendingTaskKinds"/> value, how many records wait, and the longest-waiting few (at most <see cref="OperationsDashboardService.PendingTaskSample"/>).</summary>
public sealed record DashboardPendingTaskGroup(string Kind, int Count, IReadOnlyList<DashboardPendingTask> Oldest);

public sealed record DashboardPendingTasksTile(int Total, IReadOnlyList<DashboardPendingTaskGroup> Groups);

/// <summary>FARM-06: every tile worked out now (<c>ComputedAt</c>); the filters are echoed (<c>ZoneId</c>, the period in <c>DailyRevenue</c>).</summary>
public sealed record OperationsDashboard(
    DateTimeOffset ComputedAt,
    Guid? ZoneId,
    DashboardRevenueTile DailyRevenue,
    DashboardReceivablesTile Receivables,
    DashboardOpenHarvestLotsTile OpenHarvestLots,
    DashboardWaterAlertsTile WaterAlerts,
    DashboardPendingTasksTile PendingTasks);
