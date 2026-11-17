using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>The names of the zones, customer profiles and products chosen in a sales report (REPORT-01 to REPORT-03): one read of each list that is not empty, no tracking.</summary>
internal sealed class ReportFilterLookup(OmtsDbContext db) : IReportFilterLookup
{
    public async Task<ReportChoiceNames> NamesAsync(
        IReadOnlyCollection<Guid> zoneIds, IReadOnlyCollection<Guid> customerIds, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var zones = zoneIds.ToList();
        var customers = customerIds.ToList();
        var products = productIds.ToList();
        return new ReportChoiceNames(
            zones.Count == 0 ? new Dictionary<Guid, string>() : await db.FarmingZones.AsNoTracking().Where(z => zones.Contains(z.Id)).ToDictionaryAsync(z => z.Id, z => z.Name, cancellationToken),
            customers.Count == 0 ? new Dictionary<Guid, string>() : await db.Customers.AsNoTracking().Where(c => customers.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken),
            products.Count == 0 ? new Dictionary<Guid, string>() : await db.Products.AsNoTracking().Where(p => products.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken));
    }
}
