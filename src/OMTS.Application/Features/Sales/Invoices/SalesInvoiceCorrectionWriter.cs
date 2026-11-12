using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-07 step 8 in the caller's unit of work: the invoice keeps its code, date, QR code and payments; the lines, the money, the profile, the marks and the lots change, and one audit event
/// holds the kind, the reason and the totals before and after (BR-SALE-10). The invoice is always marked changed, so a parallel correction or cancellation cannot both win.
/// </summary>
public sealed class SalesInvoiceCorrectionWriter(
    ISalesInvoiceRepository invoices,
    HarvestLotStatusService lotStatuses,
    ProductStockLedger productLedger,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string CorrectedAction = "SalesInvoice.Corrected";

    internal async Task ApplyAsync(SalesInvoice invoice, SalesInvoiceCorrectionEntries entries, SalesInvoiceCorrectionPlan plan, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = BusinessClock.ToBusinessDate(now);
        var totalBefore = invoice.Total;
        var before = Snapshot(invoice, invoice.Lines);
        var stockBefore = StockKey(invoice.Lines);

        unitOfWork.MarkChanged(invoice);
        var linesAfter = WriteLines(invoice, plan.Lines);
        // The product ledger changes only when a line does (product, lot, kg or price): the Sale rows are undone and the lines posted again, in this save.
        // A price cut, a new profile or new marks leave the rows as they are.
        if (plan.Lines.Count > 0 && !StockKey(linesAfter).SequenceEqual(stockBefore))
        {
            await productLedger.ReverseAsync(invoice.Id, cancellationToken);
            productLedger.PostSale(invoice, plan.Customer ?? invoice.Customer);
        }
        invoice.Subtotal = plan.Subtotal;
        invoice.DiscountAmount = plan.Discount;
        invoice.DiscountDecidedByOwner = plan.Discount > 0m;
        invoice.OtherCharges = plan.OtherCharges;
        invoice.PriceCutTotal = plan.PriceCutTotal;
        invoice.Total = plan.Total;
        invoice.LastCorrectionKind = entries.Kind;
        invoice.LastCorrectedAt = now;
        invoice.LastCorrectionReason = entries.Reason;
        if (entries.IsPriceCut)
        {
            invoices.AddPriceCut(new SalesInvoicePriceCut
            {
                InvoiceId = invoice.Id, Amount = entries.PriceCutAmount, CutDate = today, Reason = entries.Reason, EnteredByAccountId = currentUser.AccountId!.Value,
            });
        }

        var movedPayments = plan.CustomerChanged ? MoveToCustomer(invoice, plan.Customer!) : [];
        foreach (var profile in plan.DebtsAfter)
        {
            // Read for the debt, so a parallel payment or merge and this save cannot both win.
            unitOfWork.MarkChanged(profile.Customer);
        }

        foreach (var lot in plan.LotsToClose)
        {
            if (lotStatuses.CloseSoldWholeLot(lot, today, invoice.Id) is { } refused)
            {
                throw new InvalidOperationException($"Harvest lot {lot.LotCode} was checked for sale and cannot be closed by invoice {invoice.Code}: {refused.Message.Text}");
            }
        }

        foreach (var lot in plan.LotsToReopen)
        {
            lotStatuses.Reopen(lot, today, HarvestLotReopenCause.SoldWholeTickRemoved, invoice.Id);
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
                PriceCutAmount = entries.IsPriceCut ? entries.PriceCutAmount : (decimal?)null,
                MovedPayments = movedPayments,
                Values = Snapshot(invoice, linesAfter),
            },
        });
    }

    // Row i keeps its number, extra rows go and new rows take the next numbers: the unique line number never collides. Answers the lines as they will be (a price cut changes none).
    private List<SalesInvoiceLine> WriteLines(SalesInvoice invoice, IReadOnlyList<CorrectedLine> lines)
    {
        var existing = invoice.Lines.OrderBy(l => l.LineNumber).ToList();
        if (lines.Count == 0)
        {
            return existing;
        }

        var written = new List<SalesInvoiceLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = i < existing.Count ? existing[i] : new SalesInvoiceLine { InvoiceId = invoice.Id, LineNumber = i + 1 };
            written.Add(line);
            var entry = lines[i];
            line.ProductId = entry.Entry.ProductId;
            line.HarvestLotId = entry.Entry.LotId;
            line.Kg = entry.Entry.Kg;
            line.UnitPrice = entry.UnitPrice;
            line.ListPrice = entry.ListPrice;
            line.PriceSource = entry.PriceSource;
            line.Amount = entry.Amount;
            line.SoldWholeLot = entry.SoldWholeLot;
            line.HarvestLot = entry.Lot;
            if (i >= existing.Count)
            {
                invoices.AddLine(line);
            }
        }

        foreach (var removed in existing.Skip(lines.Count))
        {
            // Out of the collection too: a Deleted line would stay in invoice.Lines until the save, and the product ledger would post it again.
            invoice.Lines.Remove(removed);
            invoices.RemoveLine(removed);
        }

        return written;
    }

    // BR-SALE-10 (2) and SALE-07.2: the invoice and the payments that name it; the audit event keeps the earlier profile. A request belongs to a profile, so its link goes (BR-SALE-01).
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

    // Value tuples compare decimals by value, so 2.000 kg read from the database equals 2 kg sent again.
    private static List<(Guid ProductId, Guid LotId, decimal Kg, decimal UnitPrice)> StockKey(IEnumerable<SalesInvoiceLine> lines) =>
        lines.OrderBy(l => l.LineNumber).Select(l => (l.ProductId, l.HarvestLotId, l.Kg, l.UnitPrice)).ToList();

    private static object Snapshot(SalesInvoice invoice, IEnumerable<SalesInvoiceLine> lines) => new
    {
        invoice.CustomerId,
        invoice.PurchaseRequestId,
        invoice.Subtotal,
        Discount = invoice.DiscountAmount,
        invoice.OtherCharges,
        invoice.PriceCutTotal,
        invoice.Total,
        Lines = lines.OrderBy(l => l.LineNumber).Select(l => new { l.LineNumber, l.ProductId, l.HarvestLotId, l.Kg, l.UnitPrice, l.ListPrice, l.PriceSource, l.Amount, l.SoldWholeLot }).ToList(),
    };
}
