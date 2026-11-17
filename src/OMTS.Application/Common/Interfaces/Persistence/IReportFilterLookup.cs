namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The names of the zones, customer profiles and products a report was asked about; an id that is not a record has no entry.</summary>
public sealed record ReportChoiceNames(
    IReadOnlyDictionary<Guid, string> Zones, IReadOnlyDictionary<Guid, string> Customers, IReadOnlyDictionary<Guid, string> Products);

/// <summary>Checks the choices of a sales report (REPORT-01 to REPORT-03) against the records, and reads their names for the screen and the file. Read only.</summary>
public interface IReportFilterLookup
{
    /// <summary>One query for each non-empty list.</summary>
    Task<ReportChoiceNames> NamesAsync(
        IReadOnlyCollection<Guid> zoneIds, IReadOnlyCollection<Guid> customerIds, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}
