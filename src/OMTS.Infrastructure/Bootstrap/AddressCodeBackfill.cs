using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OMTS.Application.Features.AdministrativeUnits;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>
/// The seed step that gives the customers and suppliers of before the administrative units list the codes of their province and ward: the old free-text names are
/// matched to the list (see <see cref="AddressNames"/>) and the code is set where the name matches. What matches nothing keeps its text and is shown as needing a
/// new pick. It is idempotent (it only touches a name with no code) and never fails the seed: with the list out of reach, or on any fault, it says so and leaves
/// the rows as they are, for the next run. It sets the codes in place, so it leaves no audit event and no edit stamp: no one edited the profile.
/// </summary>
public sealed class AddressCodeBackfill(OmtsDbContext db, AdministrativeUnitService units, ILogger<AddressCodeBackfill> logger)
{
    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!db.Database.IsRelational())
            {
                return "Address codes: skipped (not a relational database).";
            }

            var pairs = await PairsWithoutCodeAsync(cancellationToken);
            if (pairs.Count == 0)
            {
                return "Address codes: nothing to match.";
            }

            if (await units.TryProvincesAsync(cancellationToken) is null)
            {
                logger.LogWarning("The administrative units list is out of reach: {Count} province and ward entries keep their text until the next seed", pairs.Count);
                return "Address codes: the administrative units list is not reachable; the old addresses are matched on the next seed.";
            }

            var (provinces, wards) = (0, 0);
            foreach (var (province, ward) in pairs)
            {
                var match = await units.MatchNamesAsync(province, ward, cancellationToken);
                if (match.ProvinceCode is { } provinceCode)
                {
                    provinces += await SetProvinceCodeAsync(province, provinceCode, cancellationToken);
                }

                if (match.WardCode is { } wardCode && ward is not null)
                {
                    wards += await SetWardCodeAsync(province, ward, wardCode, cancellationToken);
                }
            }

            return $"Address codes: {provinces} provinces and {wards} wards matched to the administrative units list.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A fault here must not stop the deployment: the step is optional and runs again with the next seed.
            logger.LogWarning(exception, "The address code backfill failed; it runs again with the next seed");
            return "Address codes: could not be matched this time; they are matched on the next seed.";
        }
    }

    /// <summary>The distinct names of customers and suppliers that have a name and no code. A ward is matched only within a province, so a ward alone is not listed.</summary>
    private async Task<List<(string Province, string? Ward)>> PairsWithoutCodeAsync(CancellationToken cancellationToken)
    {
        var customers = await db.Customers
            .Where(c => c.Province != null && (c.ProvinceCode == null || (c.Ward != null && c.WardCode == null)))
            .Select(c => new { Province = c.Province!, c.Ward }).Distinct().ToListAsync(cancellationToken);
        var suppliers = await db.Suppliers
            .Where(s => s.Province != null && (s.ProvinceCode == null || (s.Ward != null && s.WardCode == null)))
            .Select(s => new { Province = s.Province!, s.Ward }).Distinct().ToListAsync(cancellationToken);
        return customers.Concat(suppliers).Select(p => (p.Province, p.Ward)).Distinct().ToList();
    }

    private async Task<int> SetProvinceCodeAsync(string province, int code, CancellationToken cancellationToken) =>
        await db.Customers.Where(c => c.Province == province && c.ProvinceCode == null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ProvinceCode, (int?)code), cancellationToken)
        + await db.Suppliers.Where(s => s.Province == province && s.ProvinceCode == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.ProvinceCode, (int?)code), cancellationToken);

    private async Task<int> SetWardCodeAsync(string province, string ward, int code, CancellationToken cancellationToken) =>
        await db.Customers.Where(c => c.Province == province && c.Ward == ward && c.WardCode == null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.WardCode, (int?)code), cancellationToken)
        + await db.Suppliers.Where(s => s.Province == province && s.Ward == ward && s.WardCode == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.WardCode, (int?)code), cancellationToken);
}
