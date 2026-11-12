using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-07 – the Owner (SALES_INVOICES Edit) enters a price cut after a claim. The invoice is changed in place and sent to the trader again (SALE-06); the debt moves once, with the save (BR-DEBT-02,
/// AC-08). Sending is after the commit and its result never undoes the correction (GFR-06). A data entry error is changed with <see cref="SalesInvoiceEditService"/>.
/// </summary>
public sealed class SalesInvoiceCorrectionService(
    ISalesInvoiceRepository invoices,
    SalesInvoiceChangeGuard guard,
    SalesInvoiceCorrectionPlanner planner,
    SalesInvoiceCorrectionWriter writer,
    SalesInvoiceOwedBackNotifier owedBack,
    SalesInvoiceChangeSending sending,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<SalesInvoiceSent>> CorrectAsync(Guid invoiceId, CorrectSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetForSendingAsync(invoiceId, cancellationToken);
        if (await guard.RefusalAsync(invoice, request.RowVersion, PermissionAction.Edit, cancellationToken) is { } refused)
        {
            return refused;
        }

        var (entries, invalid, hint) = SalesInvoiceCorrectionEntryReader.Read(request);
        if (invalid.Count > 0)
        {
            return hint is null ? AppError.InvalidFields(invalid) : AppError.Invalid(invalid[0], hint);
        }

        var plan = await planner.PlanAsync(invoice!, entries!, request.ConfirmOwedBack, cancellationToken);
        if (!plan.IsSuccess)
        {
            return plan.Error!;
        }

        writer.Apply(invoice!, entries!, plan.Value!);
        await owedBack.NotifyAsync(invoice!, plan.Value!.DebtsAfter, "corrected", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Committed: the message reads the new values and paid amounts, and a failure to send it is only shown on the invoice.
        return await sending.SendCorrectedAsync(invoice!.Id);
    }
}
