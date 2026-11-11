using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Reports;

/// <summary>One zone, customer profile or product the user chose, with its name.</summary>
public sealed record ReportChoice(Guid Id, string Name);

/// <summary>
/// The checked choices of a sales report: the period, the zones the report may read and what the user chose. <see cref="ZoneIds"/> is null for every zone, else the chosen zones or, when none
/// is chosen, the zones of the user's pairs (BR-FARM-08); <see cref="Zones"/> is what the user chose, so the screen and the file can say it.
/// </summary>
public sealed record ReportFilter(
    DateOnly From, DateOnly To, IReadOnlyCollection<Guid>? ZoneIds, IReadOnlyList<ReportChoice> Zones, IReadOnlyList<ReportChoice> Customers, IReadOnlyList<ReportChoice> Products)
{
    public IReadOnlyCollection<Guid> CustomerIds => Customers.Select(c => c.Id).ToList();

    public IReadOnlyCollection<Guid> ProductIds => Products.Select(p => p.Id).ToList();
}

/// <summary>
/// What every sales report (REPORT-01 to REPORT-03) checks before it reads a figure: the right (a function and an action), the period (the current month by default, at most five years,
/// start not after end), and the zones, customers and products chosen (they must exist; a zone the user may not read is refused as in SALE-09 and HARVEST-04). MSG-GEN-01 names every
/// field to fix at once. Read only.
/// </summary>
public sealed class ReportFilterResolver(ZoneAccessGuard zoneAccess, IReportFilterLookup lookup, TimeProvider timeProvider)
{
    public const string ZoneField = "Zone";
    public const string CustomerField = "Customer";
    public const string ProductField = "Product";

    /// <summary>A longer list is not a choice anyone made on a screen, and would make the query text grow without limit.</summary>
    public const int MaxChoices = 200;

    /// <param name="otherInvalidFields">Fields the report itself found wrong (grouping, group key), named together with the ones found here.</param>
    public async Task<Result<ReportFilter>> ResolveAsync(
        string function, PermissionAction action, DateOnly? from, DateOnly? to, IReadOnlyCollection<Guid>? zoneIds, IReadOnlyCollection<Guid>? customerIds,
        IReadOnlyCollection<Guid>? productIds, IReadOnlyList<string> otherInvalidFields, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(function, action, cancellationToken);
        if (zones.IsEmpty)
        {
            return AppError.Forbidden;
        }

        var invalid = otherInvalidFields.ToList();
        var period = PurchasePeriod.Resolve(from, to, timeProvider.Today());
        if (!period.IsSuccess && period.Error!.Details?.GetValueOrDefault("field") is string periodField)
        {
            invalid.Add(periodField);
        }

        var chosenZones = (zoneIds ?? []).Distinct().ToList();
        var chosenCustomers = (customerIds ?? []).Distinct().ToList();
        var chosenProducts = (productIds ?? []).Distinct().ToList();
        var names = new ReportChoiceNames(new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), new Dictionary<Guid, string>());
        if (chosenZones.Count > MaxChoices || chosenCustomers.Count > MaxChoices || chosenProducts.Count > MaxChoices)
        {
            invalid.AddRange(new[] { (chosenZones.Count, ZoneField), (chosenCustomers.Count, CustomerField), (chosenProducts.Count, ProductField) }
                .Where(c => c.Item1 > MaxChoices).Select(c => c.Item2));
        }
        else
        {
            names = await lookup.NamesAsync(chosenZones, chosenCustomers, chosenProducts, cancellationToken);
            AddUnknown(invalid, ZoneField, chosenZones, names.Zones);
            AddUnknown(invalid, CustomerField, chosenCustomers, names.Customers);
            AddUnknown(invalid, ProductField, chosenProducts, names.Products);
        }

        if (invalid.Count > 0)
        {
            return invalid.Count == 1 ? AppError.Invalid(invalid[0]) : AppError.InvalidFields(invalid.Distinct().ToList());
        }

        // The zones exist; one the user may not read is refused, not left out, so a total is never a part of what was asked for.
        if (!chosenZones.TrueForAll(zones.Allows))
        {
            return AppError.Forbidden;
        }

        var (start, end) = period.Value;
        IReadOnlyCollection<Guid>? zoneFilter = chosenZones.Count > 0 ? chosenZones : zones.AllZones ? null : zones.ZoneIds;
        return new ReportFilter(start, end, zoneFilter, Choices(chosenZones, names.Zones), Choices(chosenCustomers, names.Customers), Choices(chosenProducts, names.Products));
    }

    private static void AddUnknown(List<string> invalid, string field, List<Guid> chosen, IReadOnlyDictionary<Guid, string> found)
    {
        if (chosen.Exists(id => !found.ContainsKey(id)))
        {
            invalid.Add(field);
        }
    }

    private static List<ReportChoice> Choices(List<Guid> chosen, IReadOnlyDictionary<Guid, string> names) =>
        chosen.Select(id => new ReportChoice(id, names[id])).ToList();
}
