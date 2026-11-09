using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-05 step 8 (and SALE-05.6/05.8 instead of 7-8) in the caller's unit of work: the Completed invoice, its lines and the cash payment Awaiting confirmation, the close of each lot whose
/// "sold the whole lot" mark is kept, and their audit rows. The profile and the linked request are marked changed, so a parallel merge or cancel and this save cannot both win.
/// </summary>
public sealed class SalesInvoiceWriter(
    ISalesInvoiceRepository invoices,
    HarvestLotStatusService lotStatuses,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string RecordType = nameof(SalesInvoice);
    public const string PaymentRecordType = nameof(CustomerPayment);
    public const string CreatedAction = "SalesInvoice.Created";
    public const string PaymentRecordedAction = "CustomerPayment.Recorded";
    public const string CodeUnusedAction = "SalesInvoice.CodeUnused";

    /// <param name="sellerOnTransfer">
    /// Set when the bank notification saves a sale paid by VietQR (GFR-08): the Zone Manager who sold it, who is then the creator, the collector of the cash part and the actor
    /// of their audit rows. The sale was checked when its code was shown, so a lot closed meanwhile stays as it is instead of refusing money that has arrived.
    /// </param>
    internal SalesInvoice Write(
        SalesInvoiceEntries entries, SalesInvoiceFacts facts, SalesInvoiceDecision decision, string code, string? paymentCode, string? deduplicationKey, Guid? sellerOnTransfer = null)
    {
        var now = timeProvider.GetUtcNow();
        var today = BusinessClock.ToBusinessDate(now);
        var creator = sellerOnTransfer ?? currentUser.AccountId!.Value;
        var invoice = new SalesInvoice
        {
            Code = code,
            InvoicedAt = now,
            InvoiceDate = today,
            ZoneId = facts.ZoneId,
            CustomerId = facts.Customer?.Id,
            Customer = facts.Customer,
            PurchaseRequestId = facts.Request?.Id,
            TakesRequestInFull = entries.TakesRequestInFull,
            DiscountAmount = entries.Discount,
            DiscountDecidedByOwner = entries.Discount > 0m,
            OtherCharges = entries.OtherCharges,
            Subtotal = decision.Subtotal,
            Total = decision.Total,
            PaymentTerm = decision.Term,
            CreatedByAccountId = creator,
            DeduplicationKey = deduplicationKey,
        };
        foreach (var line in decision.Lines)
        {
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNumber = line.Entry.LineNumber,
                ProductId = line.Product.Id,
                Product = line.Product,
                HarvestLotId = line.Lot.Id,
                HarvestLot = line.Lot,
                Kg = line.Entry.Kg,
                UnitPrice = line.UnitPrice,
                ListPrice = line.ListPrice,
                PriceSource = line.PriceSource,
                Amount = line.Amount,
                SoldWholeLot = !entries.NotSoldWholeLotIds.Contains(line.Lot.Id),
            });
        }

        invoices.Add(invoice);
        MarkReferencesChanged(facts);
        audit.Record(new AuditEntry(CreatedAction, RecordType)
        {
            RecordId = invoice.Id,
            RecordCode = invoice.Code,
            ZoneId = invoice.ZoneId,
            After = Snapshot(invoice, entries, facts),
            ActorAccountId = sellerOnTransfer,
        });

        if (paymentCode is not null)
        {
            RecordCash(invoice, decision.Cash.Recorded, paymentCode, today, creator, sellerOnTransfer);
        }

        // BR-HARVEST-04: a ticked lot is closed with the reason Sold whole lot and a leftover of 0, naming this invoice, so only it reopens the lot.
        foreach (var lot in facts.Lots.Where(l => !entries.NotSoldWholeLotIds.Contains(l.Id) && (sellerOnTransfer is null || l.IsForSale)))
        {
            // EnsureForSale (SalesInvoiceFactsReader) refused every lot no longer for sale, so a refusal here is a bug, never an answer.
            if (lotStatuses.CloseSoldWholeLot(lot, today, invoice.Id) is { } refused)
            {
                throw new InvalidOperationException($"Harvest lot {lot.LotCode} was checked for sale and cannot be closed by invoice {invoice.Code}: {refused.Message.Text}");
            }
        }

        return invoice;
    }

    /// <summary>
    /// BR-SALE-08: the codes an attempt took (outside the save, <see cref="IDocumentNumberAllocator"/>) and no invoice holds, with why, so the audit log explains the gap they leave.
    /// Added to the caller's unit of work, which must hold nothing else.
    /// </summary>
    internal void RecordUnusedCodes(string code, string? paymentCode, Guid? zoneId, string reason) =>
        audit.Record(new AuditEntry(CodeUnusedAction, RecordType)
        {
            RecordCode = code,
            ZoneId = zoneId,
            After = new { InvoiceCode = code, PaymentCode = paymentCode },
            Reason = reason,
        });

    private void RecordCash(SalesInvoice invoice, decimal amount, string code, DateOnly today, Guid collector, Guid? actor)
    {
        var payment = new CustomerPayment
        {
            Code = code,
            CustomerId = invoice.CustomerId,
            Kind = CustomerPaymentKind.Payment,
            PaymentDate = today,
            Amount = amount,
            Method = CustomerPaymentMethod.Cash,
            InvoiceId = invoice.Id,
            Invoice = invoice,
            CollectedByAccountId = collector,
            RecordedByAccountId = collector,
        };
        invoices.AddPayment(payment);
        invoice.Payments.Add(payment);
        audit.Record(new AuditEntry(PaymentRecordedAction, PaymentRecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            After = new
            {
                payment.Kind, payment.Method, payment.Amount, payment.PaymentDate, payment.CustomerId, InvoiceCode = invoice.Code, payment.CollectedByAccountId,
                Status = statusCatalog.CodeOf(payment.StatusId),
            },
            ActorAccountId = actor,
        });
    }

    private void MarkReferencesChanged(SalesInvoiceFacts facts)
    {
        // CustomerReferences: a merge at this moment conflicts, so no invoice stays on a merged profile; a trader's cancel of the request conflicts too.
        if (facts.Customer is { } customer)
        {
            unitOfWork.MarkChanged(customer);
        }

        if (facts.Request is { } request)
        {
            unitOfWork.MarkChanged(request);
        }
    }

    private object Snapshot(SalesInvoice invoice, SalesInvoiceEntries entries, SalesInvoiceFacts facts) => new
    {
        invoice.Code,
        invoice.InvoiceDate,
        invoice.ZoneId,
        invoice.CustomerId,
        WalkInCustomer = invoice.CustomerId is null,
        invoice.PurchaseRequestId,
        invoice.TakesRequestInFull,
        Lines = invoice.Lines.Select(l => new { l.LineNumber, l.ProductId, ProductName = l.Product.Name, l.HarvestLotId, LotCode = l.HarvestLot.LotCode, l.Kg, l.UnitPrice, l.ListPrice, l.PriceSource, l.Amount, l.SoldWholeLot }),
        invoice.Subtotal,
        Discount = invoice.DiscountAmount,
        invoice.DiscountDecidedByOwner,
        invoice.OtherCharges,
        invoice.Total,
        invoice.PaymentTerm,
        // The Owner's agreements confirmed at the port, kept as GBR-02 asks: "in person" approvals are not otherwise recorded.
        PayLaterAgreedBy = invoice.PaymentTerm != SalesPaymentTerm.PayLater ? null : facts.Request?.PayLaterAgreed == true ? "PurchaseRequest" : facts.ByOwner ? "Owner" : "OwnerOutsideOmts",
        SubtractedFromDebt = entries.SubtractExtraFromDebt,
        Status = statusCatalog.CodeOf(invoice.StatusId),
    };
}
