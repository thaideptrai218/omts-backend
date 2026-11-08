using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-09.2 Print: the list and its total, or one invoice, as an .xlsx file the user downloads and prints (PM default until a print layout is decided). Export in the zone of the invoice:
/// the Zone Manager of the zone and the Owner; the Accountant only views (§I.4.4, R). Changes nothing.
/// </summary>
public sealed class SalesInvoiceExportService(
    SalesInvoiceSearchService search, ISalesInvoiceRepository invoices, SalesInvoiceViews views, ZoneAccessGuard zoneAccess, ISpreadsheetWriter spreadsheets)
{
    public async Task<Result<ExportedFile>> ExportListAsync(SalesInvoiceSearchQuery query, CancellationToken cancellationToken)
    {
        var found = await search.ExportRowsAsync(query, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var matches = found.Value!;
        return Write($"sales-invoices-{matches.From:yyyyMMdd}-{matches.To:yyyyMMdd}.xlsx", SalesInvoiceExportSheets.List(matches.Rows, matches.CompletedTotal, query.CashCollectedByMe));
    }

    public async Task<Result<ExportedFile>> PrintAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.SalesInvoices, PermissionAction.Export, invoice.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return Write($"sales-invoice-{invoice.Code}.xlsx", SalesInvoiceExportSheets.Invoice(await views.ViewAsync(invoice, cancellationToken)));
    }

    private Result<ExportedFile> Write(string fileName, IReadOnlyList<SpreadsheetSheet> sheets)
    {
        try
        {
            return new ExportedFile(fileName, ISpreadsheetWriter.ContentType, spreadsheets.Write(sheets));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }
}
