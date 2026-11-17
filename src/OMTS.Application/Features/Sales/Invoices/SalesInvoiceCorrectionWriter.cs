using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-07 step 8 in the caller's unit of work, for the price cut after a claim: the invoice keeps its code, date, QR code, lines and payments; the cut is added, the total comes down (what the invoice
/// can no longer hold of the advance of its order is cut), a walk-in invoice with money to give back gets its profile, and one audit event holds the kind, the reason and the totals before and after
/// (BR-SALE-10). The product ledger does not change: the lines keep their prices. The invoice is always marked changed, so a parallel change or cancellation cannot both win. The same event name is
/// written by an edit of the invoice (<see cref="SalesInvoiceEditWriter"/>).
/// </summary>
public sealed class SalesInvoiceCorrectionWriter(
    ISalesInvoiceRepository invoices,
    SalesInvoiceAdvanceTrimmer trimmer,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string CorrectedAction = "SalesInvoice.Corrected";

    internal void Apply(SalesInvoice invoice, SalesInvoiceCorrectionEntries entries, SalesInvoiceCorrectionPlan plan)
    {
        var now = timeProvider.GetUtcNow();
        var today = BusinessClock.ToBusinessDate(now);
        var totalBefore = invoice.Total;
        var before = Snapshot(invoice);

        unitOfWork.MarkChanged(invoice);
        invoice.PriceCutTotal = plan.PriceCutTotal;
        invoice.Total = plan.Total;
        invoice.LastCorrectionKind = entries.Kind;
        invoice.LastCorrectedAt = now;
        invoice.LastCorrectionReason = entries.Reason;
        invoices.AddPriceCut(new SalesInvoicePriceCut
        {
            InvoiceId = invoice.Id, Amount = entries.PriceCutAmount, CutDate = today, Reason = entries.Reason, EnteredByAccountId = currentUser.AccountId!.Value,
        });
        trimmer.Apply(plan.Trim);

        var movedPayments = plan.CustomerChanged ? MoveToCustomer(invoice, plan.Customer!) : [];
        foreach (var profile in plan.DebtsAfter)
        {
            // Read for the debt, so a parallel payment or merge and this save cannot both win.
            unitOfWork.MarkChanged(profile.Customer);
        }

        audit.Record(new AuditEntry(CorrectedAction, SalesInvoiceWriter.RecordType)
        {
            RecordId = invoice.Id,
            RecordCode = invoice.Code,
            ZoneId = invoice.ZoneId,
            Reason = entries.Reason,
            Before = before,
            After = new
            {
                entries.Kind,
                Date = today,
                TotalBefore = totalBefore,
                TotalAfter = invoice.Total,
                PriceCutAmount = entries.PriceCutAmount,
                AdvanceTrimmed = plan.Trim.Trimmed,
                MovedPayments = movedPayments,
                Values = Snapshot(invoice),
            },
        });
    }

    // BR-SALE-10 (2) and SALE-07.2: a walk-in invoice with money to give back gets a profile, and the payments that name it move with it; the audit event keeps the earlier profile. A walk-in invoice has
    // no payment of a debt allocated to it, so no allocation stays behind with the old profile. A request belongs to a profile, so its link goes (BR-SALE-01).
    private static List<string> MoveToCustomer(SalesInvoice invoice, Customer customer)
    {
        invoice.CustomerId = customer.Id;
        invoice.Customer = customer;
        invoice.PurchaseRequestId = null;
        invoice.TakesRequestInFull = false;
        var moved = invoice.Payments.ToList();
        moved.ForEach(p => p.CustomerId = customer.Id);
        return moved.Select(p => p.Code).ToList();
    }

    private static object Snapshot(SalesInvoice invoice) => new
    {
        invoice.CustomerId,
        invoice.PurchaseRequestId,
        invoice.Subtotal,
        Discount = invoice.DiscountAmount,
        invoice.OtherCharges,
        invoice.PriceCutTotal,
        invoice.Total,
        Lines = invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new { l.LineNumber, l.ProductId, l.HarvestLotId, l.Kg, l.UnitPrice, l.ListPrice, l.PriceSource, l.Amount, l.SoldWholeLot }).ToList(),
    };
}
