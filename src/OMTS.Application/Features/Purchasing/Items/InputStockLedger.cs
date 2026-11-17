using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>One quantity to write on the ledger of an item: positive adds to the stock. <paramref name="UnitPrice"/> is the price of one base unit on the document.</summary>
public sealed record StockPosting(Guid ItemId, Guid? SeedLotLineId, decimal Quantity, decimal? UnitPrice);

/// <summary>
/// Writes the movement ledger (<see cref="InputStockMovement"/>) and keeps the cost of the items in step with it. Rows are written with the document in one save and are never
/// changed: a document that is edited or cancelled has its rows undone by reversal rows (same time, opposite quantity), and an edit then writes the new rows.
/// The cost follows the cooperative setting: Fixed sets the cost of an item that has none from the price of its first receipt; Average keeps a moving average over receipts and returns
/// (a reversal acts as the opposite document at the same price) with the quantity on hand floored at 0.
/// </summary>
public sealed class InputStockLedger(
    IInputStockRepository stock,
    IFarmingInputItemRepository items,
    ICooperativeRepository cooperatives,
    ISupplierRepository suppliers,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private readonly Dictionary<Guid, decimal> onHand = [];
    private readonly Dictionary<(Guid ItemId, Guid? LotId), decimal> buckets = [];
    private readonly HashSet<(Guid ItemId, Guid? LotId)> issued = [];
    private readonly Dictionary<Guid, FarmingInputItem> known = [];
    private InputCostMethod? costMethod;
    private bool? allowNegative;

    /// <summary>Writes the rows of a Completed receipt: every line with an item and a quantity; a Seed line names its seed lot.</summary>
    public async Task PostReceiptAsync(PurchaseReceipt receipt, CancellationToken cancellationToken)
    {
        var refs = await ItemsAsync(receipt.Lines.Select(l => l.ItemId), cancellationToken);
        var postings = receipt.Lines.OrderBy(l => l.LineNumber)
            .Where(l => l.ItemId is { } id && refs.ContainsKey(id) && l.Quantity is > 0)
            .Select(l => new StockPosting(
                l.ItemId!.Value, refs[l.ItemId!.Value].Group == InputItemGroup.Seed ? l.Id : null, l.Quantity!.Value, ReceiptLineUnits.NetPricePerBase(l)))
            .ToList();
        await PostAsync(InputStockMovementKind.Receipt, receipt.Id, receipt.Code, receipt.ReceivedAt, await SupplierNameAsync(receipt.SupplierId, cancellationToken), postings, refs, cancellationToken);
    }

    /// <summary>Writes the rows of a Recorded return: the quantities leave the stock at the price they were returned at; a line of a receipt names the seed lot it came from.</summary>
    public async Task PostReturnAsync(PurchaseReturn purchaseReturn, CancellationToken cancellationToken)
    {
        var refs = await ItemsAsync(purchaseReturn.Lines.Select(l => (Guid?)l.ItemId), cancellationToken);
        var postings = purchaseReturn.Lines.Where(l => l.Quantity > 0 && refs.ContainsKey(l.ItemId))
            .Select(l => new StockPosting(
                l.ItemId, refs[l.ItemId].Group == InputItemGroup.Seed ? l.ReceiptLineId : null, -l.Quantity, ReceiptLineUnits.PricePerBase(l.EnteredUnitPrice, l.UnitFactor)))
            .ToList();
        await PostAsync(
            InputStockMovementKind.PurchaseReturn, purchaseReturn.Id, purchaseReturn.Code, purchaseReturn.ReturnedAt,
            await SupplierNameAsync(purchaseReturn.SupplierId, cancellationToken), postings, refs, cancellationToken);
    }

    /// <summary>Writes the rows of an issue: every line leaves the stock, from its seed lot or, without one, from the quantity of the item that has no lot. The cost does not change.</summary>
    public async Task PostIssueAsync(
        InputStockMovementKind kind, Guid documentId, string documentCode, DateTimeOffset occurredAt, string? party, IReadOnlyList<StockPosting> postings, CancellationToken cancellationToken)
    {
        if (kind is not (InputStockMovementKind.Issue or InputStockMovementKind.Disposal))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only an issue or a disposal posts through here.");
        }

        var refs = await ItemsAsync(postings.Select(p => (Guid?)p.ItemId), cancellationToken);
        await PostAsync(kind, documentId, documentCode, occurredAt, party, postings, refs, cancellationToken);
        foreach (var posting in postings)
        {
            issued.Add((posting.ItemId, posting.SeedLotLineId));
        }
    }

    /// <summary>
    /// MSG-INPUT-01 for the first item (or seed lot) that issues written in this request would take below zero, unless the cooperative allows it. Call it after the rows of an issue are
    /// written, and write nothing when it answers: the check sees the balance after the whole request, so an edit that undoes and writes again is judged by its result.
    /// </summary>
    public async Task<AppError?> CheckNotNegativeAsync(CancellationToken cancellationToken)
    {
        if (issued.Count == 0 || await AllowNegativeAsync(cancellationToken))
        {
            return null;
        }

        foreach (var key in issued.Where(k => buckets.GetValueOrDefault(k) < 0m).OrderBy(k => k.ItemId).ThenBy(k => k.LotId))
        {
            var item = known[key.ItemId];
            var lotCode = key.LotId is { } lot ? (await stock.LotCodesAsync([lot], cancellationToken)).GetValueOrDefault(lot) : null;
            var balance = buckets[key];
            return AppError.Conflict(
                UserMessages.InputQuantityBelowZero(item.Name, lotCode, balance, item.Unit),
                new Dictionary<string, object?> { ["field"] = "Lines", ["itemId"] = item.Id, ["seedLotLineId"] = key.LotId, ["balanceAfter"] = balance });
        }

        return null;
    }

    /// <summary>The opening quantity of a new item: on hand from the start, at the cost the item has.</summary>
    public async Task PostOpeningAsync(FarmingInputItem item, decimal quantity, CancellationToken cancellationToken)
    {
        if (quantity <= 0)
        {
            return;
        }

        var refs = new Dictionary<Guid, FarmingInputItem> { [item.Id] = item };
        onHand[item.Id] = 0m;
        await PostAsync(
            InputStockMovementKind.Opening, item.Id, item.Code, timeProvider.GetUtcNow().ToOffset(BusinessClock.UtcOffset), null, [new StockPosting(item.Id, null, quantity, null)], refs, cancellationToken);
    }

    /// <summary>Undoes the rows of a document (or of an item's opening) that are not undone yet, and refreshes the last purchase price of a receipt's items.</summary>
    public async Task ReverseAsync(Guid documentId, bool afterReceiptRemoved, CancellationToken cancellationToken)
    {
        var rows = await stock.UnreversedOfDocumentAsync(documentId, cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        var refs = await ItemsAsync(rows.Select(r => (Guid?)r.ItemId), cancellationToken);
        await LoadOnHandAsync(refs.Keys, cancellationToken);
        var method = await MethodAsync(cancellationToken);
        foreach (var row in rows)
        {
            var item = refs[row.ItemId];
            var delta = -row.Quantity;
            var cost = AdjustedCost(item, method, InputStockMovementKind.Reversal, delta, row.TransactionPrice);
            item.CostPrice = cost;
            Write(new InputStockMovement
            {
                ItemId = row.ItemId, SeedLotLineId = row.SeedLotLineId, Kind = InputStockMovementKind.Reversal, DocumentId = row.DocumentId, DocumentCode = row.DocumentCode,
                OccurredAt = row.OccurredAt, Quantity = delta, UnitCost = cost, TransactionPrice = row.TransactionPrice, PartyName = row.PartyName, ReversesMovementId = row.Id,
            });
            onHand[row.ItemId] += delta;
        }

        if (afterReceiptRemoved)
        {
            await RefreshLastPurchasePricesAsync(refs.Values, documentId, cancellationToken);
        }
    }

    /// <summary>The quantity on hand of each item: the ledger plus the rows written in this request.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        await LoadOnHandAsync(itemIds, cancellationToken);
        return itemIds.ToDictionary(id => id, id => onHand[id]);
    }

    private async Task PostAsync(
        InputStockMovementKind kind, Guid documentId, string documentCode, DateTimeOffset occurredAt, string? party, IReadOnlyList<StockPosting> postings,
        IReadOnlyDictionary<Guid, FarmingInputItem> refs, CancellationToken cancellationToken)
    {
        await LoadOnHandAsync(postings.Select(p => p.ItemId).Distinct().ToList(), cancellationToken);
        var method = await MethodAsync(cancellationToken);
        foreach (var posting in postings)
        {
            var item = refs[posting.ItemId];
            item.CostPrice = AdjustedCost(item, method, kind, posting.Quantity, posting.UnitPrice);
            if (kind == InputStockMovementKind.Receipt && posting.UnitPrice is { } price)
            {
                item.LastPurchasePrice = price;
            }

            Write(new InputStockMovement
            {
                ItemId = posting.ItemId, SeedLotLineId = posting.SeedLotLineId, Kind = kind, DocumentId = documentId, DocumentCode = documentCode, OccurredAt = occurredAt,
                Quantity = posting.Quantity, UnitCost = item.CostPrice, TransactionPrice = posting.UnitPrice, PartyName = party,
            });
            onHand[posting.ItemId] += posting.Quantity;
        }
    }

    private decimal? AdjustedCost(FarmingInputItem item, InputCostMethod method, InputStockMovementKind kind, decimal quantity, decimal? price) =>
        InputCostRules.Adjusted(method, kind, item.CostPrice, onHand.GetValueOrDefault(item.Id), quantity, price);

    private void Write(InputStockMovement movement)
    {
        var key = (movement.ItemId, movement.SeedLotLineId);
        buckets[key] = buckets.GetValueOrDefault(key) + movement.Quantity;
        movement.RecordedAt = timeProvider.GetUtcNow();
        movement.RecordedByAccountId = currentUser.AccountId;
        stock.Add(movement);
    }

    private async Task<IReadOnlyDictionary<Guid, FarmingInputItem>> ItemsAsync(IEnumerable<Guid?> itemIds, CancellationToken cancellationToken)
    {
        var found = (await items.GetByIdsForUpdateAsync(itemIds.OfType<Guid>().Distinct().ToList(), cancellationToken)).ToDictionary(i => i.Id);
        foreach (var item in found.Values)
        {
            known[item.Id] = item;
        }

        return found;
    }

    private async Task LoadOnHandAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        var missing = itemIds.Where(id => !onHand.ContainsKey(id)).Distinct().ToList();
        foreach (var (id, quantity) in await stock.OnHandAsync(missing, cancellationToken))
        {
            onHand[id] = quantity;
        }

        foreach (var (key, quantity) in await stock.BucketBalancesAsync(missing, cancellationToken))
        {
            buckets[key] = quantity;
        }
    }

    public async Task<bool> AllowNegativeAsync(CancellationToken cancellationToken) =>
        allowNegative ??= (await cooperatives.GetCurrentAsync(cancellationToken))?.AllowNegativeInputQuantity ?? true;

    private async Task<InputCostMethod> MethodAsync(CancellationToken cancellationToken) =>
        costMethod ??= (await cooperatives.GetCurrentAsync(cancellationToken))?.InputCostMethod ?? InputCostMethod.Fixed;

    private async Task<string?> SupplierNameAsync(Guid supplierId, CancellationToken cancellationToken) =>
        (await suppliers.GetForEditAsync(supplierId, cancellationToken))?.Name;

    /// <summary>The last purchase price of the items after the receipt <paramref name="exceptReceiptId"/> no longer counts: the price of the latest other Completed receipt line, or none.</summary>
    private async Task RefreshLastPurchasePricesAsync(IEnumerable<FarmingInputItem> refs, Guid exceptReceiptId, CancellationToken cancellationToken)
    {
        var list = refs.ToList();
        var latest = await items.LatestPurchasePricesAsync(list.Select(i => i.Id).ToList(), exceptReceiptId, cancellationToken);
        foreach (var item in list)
        {
            item.LastPurchasePrice = latest.TryGetValue(item.Id, out var price) ? price : null;
        }
    }
}

