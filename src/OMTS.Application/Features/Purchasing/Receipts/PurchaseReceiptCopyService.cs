using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-01.4 – Copy fills a new receipt with the supplier, the date received and the lines of a receipt (for example one
/// just cancelled); nothing is saved until the user saves a Draft or completes it. A supplier, an item or a raft that is no
/// longer In Use, or a raft the caller may not choose (BR-PURCHASE-01), is left empty (GBR-01).
/// </summary>
public sealed class PurchaseReceiptCopyService(
    IPurchaseReceiptRepository receipts, ISupplierRepository suppliers, PurchaseReceiptValidator validator, PurchaseReceiptAccess access)
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

        var lines = source.Lines.OrderBy(l => l.LineNumber).ToList();
        var refs = await validator.LoadAsync(lines.Select(l => l.ItemId), lines.Select(l => l.RaftId), cancellationToken);
        var raftZones = await access.RaftZonesAsync(cancellationToken);
        var copied = new List<ReceiptLineInput>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var item = line.ItemId is { } itemId ? refs.Items.GetValueOrDefault(itemId) : null;
            Guid? copiedItem = item?.StatusId == StatusIds.Usage.InUse ? item.Id : null;
            if (line.ItemId is not null && copiedItem is null)
            {
                leftEmpty.Add($"Line {i + 1} item");
            }

            // The raft is judged with the line's own item, so a raft stays when only the item was discontinued.
            Guid? copiedRaft = line.RaftId is { } raftId && PurchaseReceiptValidator.RaftAllowed(item, refs.Rafts.GetValueOrDefault(raftId), raftZones) ? raftId : null;
            if (line.RaftId is not null && copiedRaft is null)
            {
                leftEmpty.Add($"Line {i + 1} raft");
            }

            copied.Add(new ReceiptLineInput(copiedItem, line.Quantity, line.UnitPrice, line.SupplierLotCode, copiedRaft));
        }

        return new PurchaseReceiptCopy(source.Id, source.Code, new SavePurchaseReceiptRequest(supplierId, source.ReceiptDate, null, copied), leftEmpty);
    }
}
