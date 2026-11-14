namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The sales invoices of the period that count in the daily revenue (BR-FARM-10): not Cancelled, at their latest values.</summary>
public sealed record DashboardRevenueFigures(decimal Total, int InvoiceCount);

/// <summary>One record waiting for the Owner: its id (to open it), its code when it has one, a short label to show it, and since when it waits.</summary>
public sealed record PendingTaskRecord(Guid Id, string? Code, string Label, DateTimeOffset Since);

/// <summary>All the records of one kind that wait for the Owner, and the longest-waiting few of them.</summary>
public sealed record PendingTaskFigures(string Kind, int Count, IReadOnlyList<PendingTaskRecord> Oldest);

/// <summary>FARM-06 figures that are read straight from the records (BR-FARM-10); nothing is stored. Each method is read only and has no zone limit of the caller: the Owner alone reads them.</summary>
public interface IOperationsDashboardQuery
{
    /// <summary>The invoices dated from <paramref name="from"/> to <paramref name="to"/> (business dates, both included), of one zone or of every zone, without the Cancelled ones.</summary>
    Task<DashboardRevenueFigures> RevenueAsync(DateOnly from, DateOnly to, Guid? zoneId, CancellationToken cancellationToken);

    /// <summary>Purchase requests and quality claims Awaiting response, payrolls Awaiting finalization, employee requests Awaiting review: always the whole cooperative, <paramref name="oldestCount"/> records of each, oldest first.</summary>
    Task<IReadOnlyList<PendingTaskFigures>> PendingTasksAsync(int oldestCount, CancellationToken cancellationToken);
}
