using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Harvest;

/// <summary>A line of the harvest form: the product, its cages and its kg. Without kg the system works it out: cages times the kg of a lồng of the product, or the kg per cage of the lot when the product has no lồng unit.</summary>
public sealed record HarvestProductLineInput(Guid? ProductId, int? CageCount, decimal? Kg);

/// <summary>A line of the lot as it will be saved.</summary>
internal sealed record ResolvedProductLine(Product Product, int Cages, decimal Kg);

/// <summary>
/// The share of each product on a harvest lot. The lines are checked against the catalogue (a product In Use, or already on the lot, once per lot; cages and kg above 0); the cages and kg of the lot are then
/// their sums, so the yield reports and the trace keep reading the lot. Each line writes one Harvest row on the product ledger; a correction undoes them by reversal rows and writes the new ones, and a removed lot
/// only undoes them. A form with no lines is the old way of recording a lot: the lot has no lines and adds nothing to any stock.
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

            if (input.CageCount is not > 0)
            {
                invalid.Add($"{field} cages");
                continue;
            }

            var kg = input.Kg ?? decimal.Round(input.CageCount.Value * CageKg(product, kgPerCage), 2);
            if (kg <= 0 || kg > PurchaseReceiptValidator.MaxQuantity || !PurchaseReceiptValidator.HasAllowedDecimals(kg))
            {
                invalid.Add($"{field} kg");
                continue;
            }

            lines.Add(new ResolvedProductLine(product, input.CageCount.Value, kg));
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
    internal static (int CageCount, decimal EstimatedKg) Totals(IReadOnlyList<ResolvedProductLine> lines) => (lines.Sum(l => l.Cages), lines.Sum(l => l.Kg));

    /// <summary>The lines of a lot that is being saved for the first time: the rows, and the Harvest rows of the ledger.</summary>
    internal void Add(HarvestLot lot, string raftCode, IReadOnlyList<ResolvedProductLine> lines)
    {
        var number = 0;
        foreach (var line in lines)
        {
            // The product is named by id only: the resolved product is an untracked read and must not join the graph of the save.
            var row = new HarvestLotProductLine { LotId = lot.Id, LineNumber = ++number, ProductId = line.Product.Id, CageCount = line.Cages, Kg = line.Kg };
            lot.ProductLines.Add(row);
            lots.AddProductLine(row);
        }

        ledger.PostHarvest(lot, raftCode, lines.Select(l => (l.Product.Id, l.Kg)));
    }

    /// <summary>The lines of a corrected lot: unchanged lines keep their ledger rows; otherwise the rows are undone and written again. True when something changed.</summary>
    internal async Task<bool> ReplaceAsync(HarvestLot lot, string raftCode, IReadOnlyList<ResolvedProductLine> lines, CancellationToken cancellationToken)
    {
        // Kg compare by value: a stored 100.000 and a typed 100 are the same line.
        string Key(IEnumerable<(Guid ProductId, int Cages, decimal Kg)> rows) =>
            string.Join(";", rows.Select(r => $"{r.ProductId}|{r.Cages}|{r.Kg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));
        var before = Key(lot.ProductLines.OrderBy(l => l.LineNumber).Select(l => (l.ProductId, l.CageCount, l.Kg)));
        var after = Key(lines.Select(l => (l.Product.Id, l.Cages, l.Kg)));
        if (before == after)
        {
            return false;
        }

        if (lot.ProductLines.Count > 0)
        {
            await ledger.ReverseAsync(lot.Id, cancellationToken);
            foreach (var old in lot.ProductLines.ToList())
            {
                lot.ProductLines.Remove(old);
                lots.RemoveProductLine(old);
            }
        }

        Add(lot, raftCode, lines);
        return true;
    }

    /// <summary>
    /// MSG-GEN-01 (field Product lines) when a correction would leave the stock of the lot out of step with its documents: lines added to a lot that already has invoice lines (those sales wrote no rows,
    /// so the harvest would add kg nothing takes off), or a product taken off a lot while it still has sales or disposals on it. Null when the lines are fine.
    /// </summary>
    internal async Task<AppError?> GuardAsync(HarvestLot lot, IReadOnlyList<ResolvedProductLine> lines, CancellationToken cancellationToken)
    {
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
