using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>A line of the harvest form: the product, its cages and its kg. Without kg the system works it out: cages times the kg of a lồng of the product, or the kg per cage of the lot when the product has no lồng unit.</summary>
public sealed record HarvestProductLineInput(Guid? ProductId, decimal? CageCount, decimal? Kg);

/// <summary>A line of the lot as it will be saved.</summary>
internal sealed record ResolvedProductLine(Product Product, decimal Cages, decimal Kg);

/// <summary>
/// The share of each product on a harvest lot. The lines are checked against the catalogue (a product In Use, or already on the lot, once per lot; cages and kg above 0); the cages and kg of the lot are then
/// their sums, so the yield reports and the trace keep reading the lot. A lot that is not weighed yet writes nothing on the product ledger and its lines may change; once it is weighed each line writes one Harvest
/// row at its weighed kg (<see cref="PostWeighed"/>), the lines can no longer change (the kg are corrected by weighing again, which undoes the Harvest rows and writes new ones), and a removed lot undoes its rows.
/// A form with no lines is the old way of recording a lot: the lot has no lines and adds nothing to any stock.
/// </summary>
public sealed class HarvestLotProductLineService(
    IProductRepository products, IHarvestLotRepository lots, IHarvestLotSalesQuery sales, IProductStockRepository stock, ProductStockLedger ledger)
{
    public const string LinesField = "Product lines";
    private const int MaxLines = 50;

    /// <summary>Checks the lines and works out their kg; null lines when the form sent none. MSG-GEN-01 names the first invalid field.</summary>
    internal async Task<(IReadOnlyList<ResolvedProductLine>? Lines, AppError? Error)> ResolveAsync(
        IReadOnlyList<HarvestProductLineInput>? inputs, bool unreadable, decimal kgPerCage, IReadOnlySet<Guid> alreadyOnLot, CancellationToken cancellationToken)
    {
        if (unreadable)
        {
            return (null, AppError.InvalidFields([LinesField]));
        }

        if (inputs is null)
        {
            return (null, null);
        }

        if (inputs.Count is 0 or > MaxLines || inputs.Any(i => i is null))
        {
            return (null, AppError.InvalidFields([LinesField]));
        }

        var found = (await products.GetByIdsAsync(inputs.Select(i => i.ProductId).OfType<Guid>().Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var invalid = new List<string>();
        var lines = new List<ResolvedProductLine>();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var field = $"Line {i + 1}";
            // A product out of use or deleted (hidden) stays allowed on a lot that already has it, so the lot can still be corrected.
            if (input.ProductId is not { } productId || !found.TryGetValue(productId, out var product)
                || ((product.IsHidden || product.StatusId != StatusIds.Usage.InUse) && !alreadyOnLot.Contains(productId)) || !seen.Add(productId))
            {
                invalid.Add($"{field} product");
                continue;
            }

            if (input.CageCount is not { } cages || !HarvestQuantityRules.CagesAreValid(cages))
            {
                invalid.Add($"{field} cages");
                continue;
            }

            var kg = input.Kg ?? HarvestQuantityRules.KgOf(cages, CageKg(product, kgPerCage));
            if (kg <= 0 || kg > PurchaseReceiptValidator.MaxQuantity || !PurchaseReceiptValidator.HasAllowedDecimals(kg))
            {
                invalid.Add($"{field} kg");
                continue;
            }

            lines.Add(new ResolvedProductLine(product, cages, kg));
        }

        return invalid.Count > 0 ? (null, AppError.InvalidFields(invalid)) : (lines, null);
    }

    /// <summary>The kg of one cage of a product: the factor of its lồng unit, else the kg per cage of the lot.</summary>
    internal static decimal CageKg(Product product, decimal kgPerCage)
    {
        var cage = product.Units.FirstOrDefault(u => !u.IsBase && IsCageUnit(u.Unit?.Name));
        return cage?.Factor ?? kgPerCage;
    }

    /// <summary>"Lồng" in any letter case and with or without the accents.</summary>
    internal static bool IsCageUnit(string? name) =>
        name is not null && string.Equals(RemoveAccents(name.Trim()), "long", StringComparison.OrdinalIgnoreCase);

    private static string RemoveAccents(string text)
    {
        var plain = text.Replace('đ', 'd').Replace('Đ', 'D').Normalize(System.Text.NormalizationForm.FormD);
        return new string(plain.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    /// <summary>The lot's totals are the sums of its lines.</summary>
    internal static (decimal CageCount, decimal EstimatedKg) Totals(IReadOnlyList<ResolvedProductLine> lines) => (lines.Sum(l => l.Cages), lines.Sum(l => l.Kg));

    /// <summary>The lines of a lot that is being saved: the rows only. The lot is not weighed, so nothing is written on the ledger.</summary>
    internal void Add(HarvestLot lot, IReadOnlyList<ResolvedProductLine> lines)
    {
        var number = 0;
        foreach (var line in lines)
        {
            // The product is named by id only: the resolved product is an untracked read and must not join the graph of the save.
            var row = new HarvestLotProductLine { LotId = lot.Id, LineNumber = ++number, ProductId = line.Product.Id, CageCount = line.Cages, Kg = line.Kg };
            lot.ProductLines.Add(row);
            lots.AddProductLine(row);
        }
    }

    /// <summary>
    /// The weighing of a lot with lines: the weighed kg of each line, and the Harvest rows of the ledger at those kg. A lot weighed before has its Harvest rows undone first, and only those: the Sale, Disposal and
    /// LotClose rows of the lot stay.
    /// </summary>
    internal async Task WeighAsync(HarvestLot lot, string raftCode, IReadOnlyDictionary<int, decimal> weighedKgByLine, CancellationToken cancellationToken)
    {
        if (lot.WeighedKg is not null)
        {
            await ledger.ReverseAsync(lot.Id, ProductStockMovementKind.Harvest, cancellationToken);
        }

        foreach (var line in lot.ProductLines)
        {
            line.WeighedKg = weighedKgByLine[line.LineNumber];
        }

        PostWeighed(lot, raftCode);
    }

    /// <summary>The Harvest rows of a weighed lot, one per product line, at the weighed kg of the line.</summary>
    private void PostWeighed(HarvestLot lot, string raftCode) =>
        ledger.PostHarvest(lot, raftCode, lot.ProductLines.OrderBy(l => l.LineNumber).Select(l => (l.ProductId, l.WeighedKg ?? throw new InvalidOperationException($"Line {l.LineNumber} of lot {lot.LotCode} has no weighed kg."))));

    /// <summary>Whether the lines of the form are the lines the lot holds, by product, cages and kg (compared by value: a stored 100.000 and a typed 100 are the same line).</summary>
    private static bool SameLines(HarvestLot lot, IReadOnlyList<ResolvedProductLine> lines)
    {
        string Key(IEnumerable<(Guid ProductId, decimal Cages, decimal Kg)> rows) =>
            string.Join(";", rows.Select(r => $"{r.ProductId}|{r.Cages.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}|{r.Kg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));
        return Key(lot.ProductLines.OrderBy(l => l.LineNumber).Select(l => (l.ProductId, l.CageCount, l.Kg))) == Key(lines.Select(l => (l.Product.Id, l.Cages, l.Kg)));
    }

    /// <summary>The lines of a corrected lot that is not weighed: the rows are replaced and nothing is written on the ledger. True when something changed.</summary>
    /// <exception cref="InvalidOperationException">The lot is weighed: <see cref="GuardAsync"/> refuses a change of its lines first, so a weighed lot never gets here with other lines.</exception>
    internal bool Replace(HarvestLot lot, IReadOnlyList<ResolvedProductLine> lines)
    {
        if (SameLines(lot, lines))
        {
            return false;
        }

        if (lot.WeighedKg is not null)
        {
            throw new InvalidOperationException($"The lines of the weighed lot {lot.LotCode} cannot be replaced.");
        }

        foreach (var old in lot.ProductLines.ToList())
        {
            lot.ProductLines.Remove(old);
            lots.RemoveProductLine(old);
        }

        Add(lot, lines);
        return true;
    }

    /// <summary>
    /// MSG-GEN-01 (field Product lines) when a correction would change the lines of a weighed lot (the kg are corrected by weighing again), or would leave the stock of the lot out of step with its documents: lines added to a lot that already has invoice lines (those sales wrote no rows,
    /// so the harvest would add kg nothing takes off), or a product taken off a lot while it still has sales or disposals on it. Null when the lines are fine.
    /// </summary>
    internal async Task<AppError?> GuardAsync(HarvestLot lot, IReadOnlyList<ResolvedProductLine> lines, CancellationToken cancellationToken)
    {
        if (lot.WeighedKg is not null && !SameLines(lot, lines))
        {
            return AppError.Invalid(LinesField, "The product lines of a weighed lot cannot be changed; weigh the lot again to correct the kg.");
        }

        if (lot.ProductLines.Count == 0)
        {
            return await sales.IsOnAnyInvoiceAsync(lot.Id, cancellationToken)
                ? AppError.Invalid(LinesField, "A lot that already has invoice lines cannot be split per product.")
                : null;
        }

        var kept = lines.Select(l => l.Product.Id).ToHashSet();
        var dropped = lot.ProductLines.Select(l => l.ProductId).Where(id => !kept.Contains(id)).ToList();
        return dropped.Count > 0 && await stock.HasUnreversedSalesOrDisposalsAsync(lot.Id, dropped, cancellationToken)
            ? AppError.Invalid(LinesField, "A product with sales or disposals on the lot cannot be taken off it.")
            : null;
    }

    /// <summary>A removed lot gives its kg back: its Harvest rows are undone.</summary>
    internal async Task ReverseAsync(HarvestLot lot, CancellationToken cancellationToken)
    {
        if (lot.ProductLines.Count > 0)
        {
            await ledger.ReverseAsync(lot.Id, cancellationToken);
        }
    }

    /// <summary>The lines of the lot for the audit log.</summary>
    internal static object Snapshot(HarvestLot lot) =>
        lot.ProductLines.OrderBy(l => l.LineNumber).Select(l => new { l.ProductId, l.CageCount, l.Kg }).ToList();
}
