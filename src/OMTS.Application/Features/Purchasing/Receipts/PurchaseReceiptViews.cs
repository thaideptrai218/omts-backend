using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>Builds the INP-03 receipt detail: lines with stocked/returnable quantities, payments, returns and money.</summary>
public sealed class PurchaseReceiptViews(IPurchaseReceiptRepository receipts, IFileStorage storage, IStatusCatalog statusCatalog)
{
    public async Task<PurchaseReceiptView> ViewAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var r = await receipts.GetForEditAsync(receiptId, cancellationToken) ?? throw new InvalidOperationException($"Receipt {receiptId} not found.");
        var stocked = await receipts.StockedAsync(r.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var returned = r.Returns.SelectMany(rt => rt.Lines).GroupBy(l => l.ReceiptLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var figures = PurchaseMoney.Of(r);
        var photos = new List<string>();
        foreach (var key in r.InvoicePhotoUrls)
        {
            photos.Add(await storage.GetReadUrlAsync(key, cancellationToken: cancellationToken));
        }

        return new PurchaseReceiptView(
            r.Id,
            r.Code,
            new SupplierRef(r.Supplier.Id, r.Supplier.Name, r.Supplier.PhoneNumber),
            new ZoneRef(r.Zone.Id, r.Zone.Code, r.Zone.Name),
            r.Raft is null ? null : new RaftRef(r.Raft.Id, r.Raft.Code),
            r.ReceiptDate,
            figures.Subtotal, figures.Discount, figures.Payable, figures.Returned, figures.Paid, figures.Refunded, figures.Outstanding,
            r.AmountPaidNow, r.PaidNowMethod, r.Notes, photos,
            r.StatusId, statusCatalog.DisplayNameOf(r.StatusId), r.CompletedAt, r.CancelledAt, r.CancelReason, r.CopiedFromReceiptId,
            r.Lines.OrderBy(l => l.Item.Code).Select(l =>
            {
                var (stockedQuantity, unknown) = stocked.GetValueOrDefault(l.Id);
                var lineReturned = returned.GetValueOrDefault(l.Id);
                return new ReceiptLineView(
                    l.Id, l.ItemId, l.Item.Code, l.Item.Name, l.Item.Group, l.Unit, l.Quantity, l.UnitPrice, l.LineAmount,
                    l.SupplierLotCode, l.ActualSource, l.SeedLot?.Id, l.SeedLot?.InternalCode,
                    unknown ? l.Quantity : stockedQuantity, lineReturned, Returnable(l, stockedQuantity, unknown, lineReturned));
            }).ToList(),
            r.Payments.OrderBy(p => p.PaymentDate).Select(p => new ReceiptPaymentView(
                p.Id, p.Amount, p.PaymentDate, p.Method, p.ReferenceCode, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId))).ToList(),
            r.Returns.OrderBy(rt => rt.ReturnDate).Select(rt => new ReceiptReturnView(
                rt.Id, rt.Code, rt.ReturnDate, rt.Reason, rt.Lines.Sum(l => l.ReturnValue), rt.RefundAmount, rt.DebtReductionAmount)).ToList(),
            RowVersionGuard.Encode(r.RowVersion));
    }

    /// <summary>
    /// Received − stocked − already returned. A seeding of the lot without a quantity counts as fully stocked,
    /// so nothing of that line can be returned (assumption of the ticket).
    /// </summary>
    public static decimal Returnable(PurchaseReceiptLine line, decimal stocked, bool unknownStocked, decimal returned) =>
        unknownStocked ? 0 : Math.Max(0, line.Quantity - stocked - returned);
}
