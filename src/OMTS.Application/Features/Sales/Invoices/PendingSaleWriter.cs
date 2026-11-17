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
    public const string EndOfDayStopReason = $"{StopReason}: the transfer did not arrive by the end of the business day";

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
    public void Stop(PendingSale sale) => Stop(sale, currentUser.AccountId, StopReason);

    /// <summary>
    /// A sale still awaiting its transfer when its business day ended is stopped by the system (council of 03/11, PM answer 12/11/2026, OMTS-224 [3.4]): the same end as the Stop of the
    /// port (BR-SALE-08: the code in the audit log, nothing saved on the invoice side), the actor the system (no account), the reason saying why.
    /// </summary>
    public void StopAtEndOfDay(PendingSale sale) => Stop(sale, by: null, EndOfDayStopReason);

    /// <summary>
    /// The transfer arrived but the invoice can no longer be saved: the sale is stopped by the system, its code written to the audit log with why, and the transfer is kept
    /// without a customer by the caller (GFR-08).
    /// </summary>
    public void StopOnTransfer(PendingSale sale, string why) =>
        Stop(sale, by: null, $"{StopReason}: the transfer arrived, but the sale could not be saved: {why}");

    private void Stop(PendingSale sale, Guid? by, string reason)
    {
        End(sale, PendingSaleStatus.Stopped, invoiceId: null, by);
        audit.Record(new AuditEntry(StoppedAction, SalesInvoiceWriter.RecordType)
        {
            RecordCode = sale.Code,
            ZoneId = sale.ZoneId,
            Reason = reason,
            After = new { InvoiceCode = sale.Code, sale.CustomerId, sale.Total, sale.CashAmount, sale.ToPayAmount, sale.CreatedByAccountId },
        });
    }
}
