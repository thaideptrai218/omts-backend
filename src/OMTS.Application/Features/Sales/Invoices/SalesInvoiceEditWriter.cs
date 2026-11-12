using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The edit of an invoice in the caller's unit of work, like the edit of a purchase receipt: the invoice keeps its date, QR code, payments and customer; its code, lines, money and note change, the
/// product ledger changes only for a line that changed (the Sale rows are undone and the lines posted again), a lot whose "sold the whole lot" tick changed is closed or reopened, what the invoice can no
/// longer hold of the advance of its order is cut, the order moves along (it completes when every line is delivered; it never reopens because an invoice got smaller), and one audit event holds the totals
/// and the lines before and after. The invoice is always marked changed, so a parallel edit or cancellation cannot both win.
/// </summary>
public sealed class SalesInvoiceEditWriter(
    ISalesInvoiceRepository invoices,
    ISalesOrderRepository orders,
    HarvestLotStatusService lotStatuses,
    ProductStockLedger productLedger,
    SalesInvoiceAdvanceTrimmer trimmer,
    SalesOrderProgress orderProgress,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    internal async Task ApplyAsync(SalesInvoice invoice, SalesInvoiceEditPlan plan, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = BusinessClock.ToBusinessDate(now);
        var totalBefore = invoice.Total;
        var before = Snapshot(invoice, invoice.Lines);
        var stockBefore = StockKey(invoice.Lines);

        unitOfWork.MarkChanged(invoice);
        var linesAfter = WriteLines(invoice, plan.Lines);
        // The product ledger changes only when a line does (product, lot, kg or the price of a kg): the Sale rows are undone and the lines posted again, in this save.
        if (!StockKey(linesAfter).SequenceEqual(stockBefore))
        {
            await productLedger.ReverseAsync(invoice.Id, cancellationToken);
            productLedger.PostSale(invoice, invoice.Customer);
        }

        invoice.Code = plan.Code;
        invoice.Note = plan.Note;
        invoice.Subtotal = plan.Subtotal;
        invoice.DiscountAmount = plan.Discount;
        invoice.DiscountDecidedByOwner = plan.Discount > 0m;
        invoice.OtherCharges = plan.OtherCharges;
        invoice.Total = plan.Total;
        invoice.LastCorrectionKind = SalesInvoiceCorrectionKind.DataEntryError;
        invoice.LastCorrectedAt = now;
        invoice.LastCorrectionReason = null;
        trimmer.Apply(plan.Trim);
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

        if (plan.Order is { } order)
        {
            await CompleteOrderWhenDeliveredAsync(invoice, order, cancellationToken);
        }

        audit.Record(new AuditEntry(SalesInvoiceCorrectionWriter.CorrectedAction, SalesInvoiceWriter.RecordType)
        {
            RecordId = invoice.Id,
            RecordCode = invoice.Code,
            ZoneId = invoice.ZoneId,
            Before = before,
            After = new
            {
                Kind = SalesInvoiceCorrectionKind.DataEntryError,
                Date = today,
                TotalBefore = totalBefore,
                TotalAfter = invoice.Total,
                AdvanceTrimmed = plan.Trim.Trimmed,
                Values = Snapshot(invoice, linesAfter),
            },
        });
    }

    // The order is read for what the other invoices delivered, and marked changed so a parallel invoice, finish or cancel of the same order and this save cannot both win.
    private async Task CompleteOrderWhenDeliveredAsync(SalesInvoice invoice, SalesOrder order, CancellationToken cancellationToken)
    {
        unitOfWork.MarkChanged(order);
        var others = await orders.DeliveredByLineAsync([order.Id], invoice.Id, cancellationToken);
        var delivered = SalesInvoiceOrderShare.DeliveredWith(others, invoice.Lines.Where(l => l.SalesOrderLineId is not null).Select(l => (l.SalesOrderLineId!.Value, l.Quantity)));
        if (SalesInvoiceOrderShare.AllDelivered(order.Lines.Select(l => (l.Id, l.Quantity)), delivered))
        {
            orderProgress.OnInvoiceSaved(order, completes: true, byUser: false);
        }
    }

    // Row i keeps its number, extra rows go and new rows take the next numbers: the unique line number never collides. Answers the lines as they will be.
    private List<SalesInvoiceLine> WriteLines(SalesInvoice invoice, IReadOnlyList<EditedLine> lines)
    {
        var existing = invoice.Lines.OrderBy(l => l.LineNumber).ToList();
        var written = new List<SalesInvoiceLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = i < existing.Count ? existing[i] : new SalesInvoiceLine { InvoiceId = invoice.Id, LineNumber = i + 1 };
            written.Add(line);
            var edited = lines[i];
            line.ProductId = edited.Entry.ProductId;
            line.HarvestLotId = edited.Entry.LotId;
            line.SalesOrderLineId = edited.Resolved.OrderLine?.Id;
            line.UnitId = edited.Resolved.Unit.UnitId;
            line.UnitCode = edited.Resolved.Unit.Code;
            line.UnitName = edited.Resolved.Unit.Name;
            line.Factor = edited.Resolved.Unit.Factor;
            line.Quantity = edited.Resolved.Quantity;
            line.Kg = edited.Entry.Kg;
            line.UnitPrice = edited.UnitPrice;
            line.ListPrice = edited.ListPrice;
            line.PriceSource = edited.PriceSource;
            line.Amount = edited.Amount;
            line.SoldWholeLot = edited.SoldWholeLot;
            line.HarvestLot = edited.Lot;
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

    // Value tuples compare decimals by value, so 2.000 kg read from the database equals 2 kg sent again.
    private static List<(Guid ProductId, Guid LotId, decimal Kg, decimal PricePerKg)> StockKey(IEnumerable<SalesInvoiceLine> lines) =>
        lines.OrderBy(l => l.LineNumber).Select(l => (l.ProductId, l.HarvestLotId, l.Kg, InvoiceMoney.PricePerKg(l.Kg, l.Quantity, l.UnitPrice, l.Amount))).ToList();

    private static object Snapshot(SalesInvoice invoice, IEnumerable<SalesInvoiceLine> lines) => new
    {
        invoice.Code,
        invoice.Note,
        invoice.CustomerId,
        invoice.Subtotal,
        Discount = invoice.DiscountAmount,
        invoice.OtherCharges,
        invoice.PriceCutTotal,
        invoice.Total,
        Lines = lines.OrderBy(l => l.LineNumber).Select(l => new
        {
            l.LineNumber, l.ProductId, l.HarvestLotId, l.SalesOrderLineId, l.UnitId, l.Quantity, l.Kg, l.UnitPrice, l.ListPrice, l.PriceSource, l.Amount, l.SoldWholeLot,
        }).ToList(),
    };
}
