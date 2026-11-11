using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-01.4 – Copy fills a new receipt with the supplier, the time received, the zone, the discount and the lines (with their discounts) of a receipt (for example one
/// just cancelled); nothing is saved until the user saves a Draft or completes it. A supplier, a zone, an item or a raft that is no
/// longer In Use, or a raft the caller may not choose (BR-PURCHASE-01), is left empty (GBR-01); a line whose item is left
/// empty loses its raft too. The copy as returned can be saved as a Draft once it has a supplier (a Draft needs one).
/// </summary>
public sealed class PurchaseReceiptCopyService(
    IPurchaseReceiptRepository receipts, ISupplierRepository suppliers, IFarmingZoneRepository zones, PurchaseReceiptValidator validator, PurchaseReceiptAccess access)
{
    public async Task<Result<PurchaseReceiptCopy>> CopyAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var source = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (source is null)
        {
            return AppError.NotFound;
        }

        var leftEmpty = new List<string>();
        Guid? supplierId = source.SupplierId;
        if (await suppliers.GetForEditAsync(source.SupplierId, cancellationToken) is not { StatusId: StatusIds.Usage.InUse })
        {
            supplierId = null;
            leftEmpty.Add("Supplier");
        }

        Guid? zoneId = source.ZoneId;
        if (zoneId is { } id && !(await zones.GetByIdsAsync([id], cancellationToken)).Any(z => z.StatusId == StatusIds.Usage.InUse))
        {
            zoneId = null;
            leftEmpty.Add("Zone");
        }

        var lines = source.Lines.OrderBy(l => l.LineNumber).ToList();
        var refs = await validator.LoadAsync(lines.Select(l => l.ItemId), lines.Select(l => l.RaftId), cancellationToken);
        var raftZones = await access.RaftZonesAsync(cancellationToken);
        var copied = new List<ReceiptLineInput>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var item = line.ItemId is { } itemId ? refs.Items.GetValueOrDefault(itemId) : null;
            Guid? copiedItem = item?.StatusId == StatusIds.Usage.InUse ? item.Id : null;
            Guid? copiedRaft = null;
            if (line.ItemId is not null && copiedItem is null)
            {
                // The raft goes with the item: the user picks both again (the raft depends on the group of the item).
                leftEmpty.Add($"Line {i + 1} item");
            }
            else if (line.RaftId is { } raftId)
            {
                copiedRaft = PurchaseReceiptValidator.RaftAllowed(item, refs.Rafts.GetValueOrDefault(raftId), raftZones, itemRequired: item is not null) ? raftId : null;
                if (copiedRaft is null)
                {
                    leftEmpty.Add($"Line {i + 1} raft");
                }
            }

            copied.Add(new ReceiptLineInput(copiedItem, line.EnteredQuantity, line.EnteredUnitPrice, line.SupplierLotCode, copiedRaft, line.EnteredDiscount, ItemUnitId: copiedItem is null ? null : line.ItemUnitId));
        }

        // The receiver is left out: the new receipt is taken in by whoever saves it unless they choose someone.
        return new PurchaseReceiptCopy(
            source.Id, source.Code,
            new SavePurchaseReceiptRequest(supplierId, source.ReceiptDate, null, copied, Discount: source.Discount, ZoneId: zoneId, ReceivedAt: source.ReceivedAt),
            leftEmpty);
    }
}

