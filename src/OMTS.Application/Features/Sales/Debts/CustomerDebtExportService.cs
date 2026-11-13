using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>DEBT-03.2: the debt list and, when a customer is chosen, its statement of the period and its unpaid invoices, as an .xlsx file (what the screen shows).</summary>
public sealed class CustomerDebtExportService(CustomerDebtService debts, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    public async Task<Result<ExportedFile>> ExportAsync(CustomerDebtExportQuery query, CancellationToken cancellationToken)
    {
        CustomerDebtStatement? statement = null;
        if (query.CustomerId is { } customerId)
        {
            var found = await debts.StatementAsync(customerId, query.From, query.To, cancellationToken);
            if (!found.IsSuccess)
            {
                return found.Error!;
            }

            statement = found.Value;
        }
        else if (PurchasePeriod.Resolve(query.From, query.To, timeProvider.Today()) is { IsSuccess: false } wrongPeriod)
        {
            // The list does not use the period, but a wrong period gives no file, as with a customer (DEBT-03.0.E1).
            return wrongPeriod.Error!;
        }

        var sheets = new List<SpreadsheetSheet> { ListSheet(await debts.RowsAsync(query.Search, cancellationToken)) };
        if (statement is not null)
        {
            sheets.Add(StatementSheet(statement));
            sheets.Add(UnpaidSheet(statement));
        }

        try
        {
            var name = statement is null
                ? $"customer-debts-{timeProvider.Today():yyyyMMdd}.xlsx"
                : $"customer-debts-{statement.From:yyyyMMdd}-{statement.To:yyyyMMdd}.xlsx";
            return new ExportedFile(name, ISpreadsheetWriter.ContentType, spreadsheets.Write(sheets));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private static SpreadsheetSheet ListSheet(CustomerDebtRows list)
    {
        var sheetRows = list.Rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Customer.Name, r.Customer.PhoneNumber, r.Debt, r.DebtAgeDays, Day(r.OldestUnpaidDate),
        ]).ToList();
        // The cooperative's totals, as on the screen: of every profile, whatever the search.
        sheetRows.Add(["Total receivables", null, list.TotalReceivables, null, null]);
        sheetRows.Add(["Total money owed back", null, list.TotalOwedBack, null, null]);
        return new SpreadsheetSheet("Customer debts", ["Customer", "Phone", "Debt", "Debt age (days)", "Oldest unpaid invoice"], sheetRows);
    }

    private static SpreadsheetSheet StatementSheet(CustomerDebtStatement statement)
    {
        var sheetRows = new List<IReadOnlyList<object?>>
        {
            new object?[] { Day(statement.From), "Debt at the start", statement.Customer.Name, null, null, null, null, statement.OpeningDebt },
        };
        // The internal data of the payments, as the staff statement shows it (BR-DEBT-04); empty for invoices. Each invoice is followed by its corrections and price cuts, as on the screen.
        sheetRows.AddRange(statement.Entries.SelectMany(e => (e.Corrections ?? []).Select(c => CorrectionRow(e, c)).Prepend(EntryRow(e))));
        sheetRows.Add(new object?[] { Day(statement.To), "Debt at the end", statement.Customer.Name, null, null, null, null, statement.ClosingDebt });
        return new SpreadsheetSheet(
            "Statement",
            ["Date", "Entry", "Code", "Invoice", "Method", "Status", "Amount", "Debt after", "Recorded by", "Confirmed by", "Collected by", "Note", "Cancel reason", "Total before", "Total after"],
            sheetRows);
    }

    private static IReadOnlyList<object?> EntryRow(CustomerStatementEntry e) =>
    [
        Day(e.Date), KindText(e.Kind), e.Code, e.InvoiceCode, e.Method?.ToString(), Status(e), e.Amount, e.DebtAfter,
        e.RecordedBy?.Name, e.ConfirmedBy?.Name, e.CollectedBy?.Name, e.Note, e.CancelReason,
    ];

    // BR-DEBT-04: one row under the invoice, the amount of the invoice stays on the invoice's own row; the reason goes in the Note column.
    private static IReadOnlyList<object?> CorrectionRow(CustomerStatementEntry invoice, SalesInvoiceCorrectionView correction) =>
    [
        Day(correction.Date), CorrectionText(correction.Kind), null, invoice.Code, null, null, null, null, null, null, null, correction.Reason, null, correction.TotalBefore, correction.TotalAfter,
    ];

    private static string CorrectionText(SalesInvoiceCorrectionKind kind) =>
        kind == SalesInvoiceCorrectionKind.PriceCutAfterClaim ? "Price cut after claim" : "Data entry correction";

    private static SpreadsheetSheet UnpaidSheet(CustomerDebtStatement statement) => new(
        "Unpaid invoices",
        ["Invoice", "Invoice date", "Total", "Covered", "Unpaid", "Age (days)"],
        statement.UnpaidInvoices.Select(i => (IReadOnlyList<object?>)[i.Code, Day(i.InvoiceDate), i.Total, i.Covered, i.Unpaid, i.AgeDays]).ToList());

    private static DateTime? Day(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue);

    /// <summary>BR-DEBT-04: a cancelled entry with its cancel date (business date) when it has one.</summary>
    private static string? Status(CustomerStatementEntry e) => e.Cancelled
        ? e.CancelledAt is { } at ? "Cancelled " + BusinessClock.ToBusinessDate(at).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "Cancelled"
        : e.AwaitingConfirmation ? "Awaiting confirmation" : e.Pending ? "Pending transfer" : null;

    private static string KindText(CustomerStatementEntryKind kind) => kind switch
    {
        CustomerStatementEntryKind.Invoice => "Sales invoice",
        CustomerStatementEntryKind.PriceCut => "Price cut",
        CustomerStatementEntryKind.Return => "Sales return",
        CustomerStatementEntryKind.Payment => "Payment",
        CustomerStatementEntryKind.Discount => "Payment discount",
        CustomerStatementEntryKind.Adjustment => "Adjustment",
        _ => "Money paid back",
    };
}
