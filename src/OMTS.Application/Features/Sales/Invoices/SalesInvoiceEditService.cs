using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The Owner (SALES_INVOICES Edit) changes a Completed invoice directly, like a purchase receipt: the code, the lines, the discount, the other charges and the note. The customer and the zone stay.
/// The debt moves once, with the save, by the difference of the totals (BR-DEBT-02, AC-08); the payments are not touched. The invoice is sent to the trader again (SALE-06) after the commit, and its
/// result never undoes the edit (GFR-06).
/// </summary>
public sealed class SalesInvoiceEditService(
    ISalesInvoiceRepository invoices,
    SalesInvoiceChangeGuard guard,
    SalesInvoiceEditPlanner planner,
    SalesInvoiceEditWriter writer,
    SalesInvoiceOwedBackNotifier owedBack,
    SalesInvoiceChangeSending sending,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<SalesInvoiceSent>> EditAsync(Guid invoiceId, EditSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetForSendingAsync(invoiceId, cancellationToken);
        if (await guard.RefusalAsync(invoice, request.RowVersion, Authorization.Models.PermissionAction.Edit, cancellationToken) is { } refused)
        {
            return refused;
        }

        var (entries, invalid) = Read(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var plan = await planner.PlanAsync(invoice!, entries!, cancellationToken);
        if (!plan.IsSuccess)
        {
            return plan.Error!;
        }

        await writer.ApplyAsync(invoice!, plan.Value!, cancellationToken);
        await owedBack.NotifyAsync(invoice!, plan.Value!.DebtsAfter, "edited", cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Committed. An edit sends nothing to the trader again; the answer shows the sending status the invoice already has.
        return await sending.CurrentAsync(invoice!.Id);
    }

    /// <summary>The shape of each entry (MSG-GEN-01 names every field to fix); the version is needed by the guard before this.</summary>
    private static (SalesInvoiceEditEntries? Entries, List<string> Invalid) Read(EditSalesInvoiceRequest request)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        var lines = SalesInvoiceEntryReader.ReadLines(request.Lines, invalid);
        var notSoldWhole = (request.NotSoldWholeLotIds ?? []).ToHashSet();
        if (!notSoldWhole.IsSubsetOf(lines.Select(l => l.LotId)))
        {
            invalid.Add("Sold the whole lot");
        }

        if (request.Discount is not null && !InvoiceMoney.IsWholeAmount(request.Discount))
        {
            invalid.Add("Discount");
        }

        if (request.OtherCharges is not null && !InvoiceMoney.IsWholeAmount(request.OtherCharges))
        {
            invalid.Add("Other charges");
        }

        var note = SalesInvoiceEntryReader.ReadNote(request.Note, invalid);
        return invalid.Count > 0
            ? (null, invalid.Distinct().ToList())
            : (new SalesInvoiceEditEntries(DocumentCodes.Typed(request.Code), lines, notSoldWhole, request.Discount, request.OtherCharges, note, request.Note is not null, request.ConfirmOwedBack), invalid);
    }
}
