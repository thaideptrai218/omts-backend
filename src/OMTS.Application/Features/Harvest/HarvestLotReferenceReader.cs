using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// What the lot screen links to and shows besides the lot: the seed lots and nursery batch of its cycle, the orders its harvest plan serves, the invoice that closed it, and the kg of each of its products
/// on the product ledger. A fixed number of reads, whatever the size of the cycle or the ledger.
/// </summary>
public sealed class HarvestLotReferenceReader(IHarvestLotReferenceQuery references, IHarvestPlanRepository harvestPlans, IProductStockRepository stock)
{
    /// <summary>The links of one lot. <paramref name="plan"/> is the plan the lot was recorded from, with the ids of the orders it serves; null for a lot outside a plan.</summary>
    public async Task<HarvestLotReferences> ReferencesAsync(HarvestLot lot, PlanOfLot? plan, CancellationToken cancellationToken)
    {
        var origin = await references.OriginOfCycleAsync(lot.CycleId, lot.Cycle?.NurseryBatchId, cancellationToken);
        var orders = plan is { OrderIds.Count: > 0 }
            ? (await harvestPlans.OrdersByIdsAsync(plan.OrderIds, cancellationToken)).OrderBy(o => o.Code, StringComparer.Ordinal).Select(o => new HarvestLotLinkRef(o.Id, o.Code)).ToList()
            : [];
        HarvestLotLinkRef? closedBy = null;
        if (lot.ClosedBySalesInvoiceId is { } invoiceId && (await references.InvoicesAsync([invoiceId], cancellationToken)).TryGetValue(invoiceId, out var invoice))
        {
            closedBy = new HarvestLotLinkRef(invoice.Id, invoice.Code);
        }

        return new HarvestLotReferences(
            origin.SeedLots.Select(s => new HarvestLotSeedLotRef(s.ReceiptId, s.ReceiptCode, s.LineNumber, DocumentCodes.SeedLotCode(s.ReceiptCode, s.LineNumber), s.ItemCode, s.ItemName)).ToList(),
            origin.NurseryBatch is { } batch ? new HarvestLotLinkRef(batch.Id, batch.Code) : null, orders, closedBy);
    }

    /// <summary>The Sale rows of these ledger rows with the order of their invoice: what the movements of a lot show next to a sale. A row of another kind is absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> OrderCodesOfSalesAsync(IEnumerable<ProductStockMovement> rows, CancellationToken cancellationToken)
    {
        var invoiceIds = rows.Where(r => r.Kind is ProductStockMovementKind.Sale or ProductStockMovementKind.Reversal && r.DocumentId is not null).Select(r => r.DocumentId!.Value).Distinct().ToList();
        var invoices = await references.InvoicesAsync(invoiceIds, cancellationToken);
        return invoices.Values.Where(i => i.SalesOrderCode is not null).ToDictionary(i => i.Id, i => i.SalesOrderCode!);
    }

    /// <summary>The kg of each product of the lot on the ledger (see <see cref="ProductBalances"/>), from one read of the ledger of the lot.</summary>
    public async Task<IReadOnlyList<HarvestLotProductBalance>> ProductBalancesAsync(
        HarvestLot lot, IReadOnlyDictionary<Guid, Product> catalogue, CancellationToken cancellationToken)
    {
        var rows = await stock.ForLotAsync(lot.Id, cancellationToken);
        var lines = lot.ProductLines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var product = l.Product ?? catalogue.GetValueOrDefault(l.ProductId);
            return (l.ProductId, product?.Code ?? string.Empty, product?.Name ?? string.Empty);
        });
        return ProductBalances(lines, rows);
    }

    /// <summary>
    /// The kg of each product of the lot on the ledger: the lines of the lot in line order, then any other product the ledger holds on it. A reversal counts under the kind of the row it undoes, so a sale
    /// that was cancelled counts for nothing. A product with no row yet (a lot waiting for its weighing) shows zeros.
    /// </summary>
    public static IReadOnlyList<HarvestLotProductBalance> ProductBalances(IEnumerable<(Guid ProductId, string Code, string Name)> lines, IReadOnlyList<ProductStockMovement> rows)
    {
        var kindOf = rows.ToDictionary(r => r.Id, r => r.Kind);
        ProductStockMovementKind Effective(ProductStockMovement m) =>
            m.Kind == ProductStockMovementKind.Reversal && m.ReversesMovementId is { } undone && kindOf.TryGetValue(undone, out var original) ? original : m.Kind;

        var products = lines.GroupBy(l => l.ProductId).Select(g => g.First()).ToList();
        foreach (var row in rows.Where(r => products.All(p => p.ProductId != r.ProductId)).GroupBy(r => r.ProductId).Select(g => g.First()))
        {
            products.Add((row.ProductId, row.Product?.Code ?? string.Empty, row.Product?.Name ?? string.Empty));
        }

        return products.Select(p =>
        {
            var own = rows.Where(r => r.ProductId == p.ProductId).ToList();
            decimal Sum(ProductStockMovementKind kind) => own.Where(r => Effective(r) == kind).Sum(r => r.QuantityKg);
            return new HarvestLotProductBalance(p.ProductId, p.Code, p.Name, Sum(ProductStockMovementKind.Harvest), -Sum(ProductStockMovementKind.Sale), own.Sum(r => r.QuantityKg), Sum(ProductStockMovementKind.Return));
        }).ToList();
    }
}
