using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>Holds a sale while its VietQR transfer is awaited (SALE-05 step 7) and ends it, in the caller's unit of work.</summary>
public sealed class PendingSaleWriter(IPendingSaleRepository sales, AuditLogService audit, IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider)
{
    public const string StoppedAction = "SalesInvoice.SaleStopped";
    public const string StopReason = "Stop sale";

    internal PendingSale Add(CreateSalesInvoiceRequest request, SalesInvoiceFacts facts, SalesInvoiceDecision decision, string code, string? deduplicationKey)
    {
        var sale = new PendingSale
        {
            Code = code,
            ZoneId = facts.ZoneId,
            CustomerId = facts.Customer?.Id,
            Customer = facts.Customer,
            CreatedByAccountId = currentUser.AccountId!.Value,
            Total = decision.Total,
            CashAmount = decision.Cash.Recorded,
            ToPayAmount = decision.VietQrAmount,
            SaleJson = PendingSaleSnapshot.Of(request, facts, decision).ToJson(),
            DeduplicationKey = deduplicationKey,
        };
        sales.Add(sale);
        // CustomerReferences: a merge at this moment conflicts, so no sale stays on a merged profile.
        if (facts.Customer is { } customer)
        {
            unitOfWork.MarkChanged(customer);
        }

        return sale;
    }

    /// <summary>The sale leaves Awaiting; its row is marked changed, so of a transfer, a change of payment and a stop at the same moment only one is saved.</summary>
    /// <param name="by">The person who ended it; null for the bank notification.</param>
    public void End(PendingSale sale, PendingSaleStatus status, Guid? invoiceId, Guid? by)
    {
        sale.Status = status;
        sale.InvoiceId = invoiceId;
        sale.EndedAt = timeProvider.GetUtcNow();
        sale.EndedByAccountId = by;
        unitOfWork.MarkChanged(sale);
    }

    /// <summary>SALE-05.0.E4 step 3, BR-SALE-08: nothing is saved but the code, written to the audit log with the reason Stop sale.</summary>
    public void Stop(PendingSale sale)
    {
        End(sale, PendingSaleStatus.Stopped, invoiceId: null, currentUser.AccountId);
        audit.Record(new AuditEntry(StoppedAction, SalesInvoiceWriter.RecordType)
        {
            RecordCode = sale.Code,
            ZoneId = sale.ZoneId,
            Reason = StopReason,
            After = new { InvoiceCode = sale.Code, sale.CustomerId, sale.Total, sale.CashAmount, sale.ToPayAmount, sale.CreatedByAccountId },
        });
    }
}
