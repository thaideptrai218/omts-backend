using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Receipts;

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

    private static SpreadsheetSheet ListSheet(IReadOnlyList<CustomerDebtRow> rows)
    {
        var sheetRows = rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Customer.Name, r.Customer.PhoneNumber, r.Debt, r.DebtAgeDays, Day(r.OldestUnpaidDate),
        ]).ToList();
        sheetRows.Add(["Total receivables", null, rows.Sum(r => CustomerDebtMath.Receivable(r.Debt)), null, null]);
        sheetRows.Add(["Total money owed back", null, rows.Sum(r => CustomerDebtMath.OwedBack(r.Debt)), null, null]);
        return new SpreadsheetSheet("Customer debts", ["Customer", "Phone", "Debt", "Debt age (days)", "Oldest unpaid invoice"], sheetRows);
    }

    private static SpreadsheetSheet StatementSheet(CustomerDebtStatement statement)
    {
        var sheetRows = new List<IReadOnlyList<object?>>
        {
            new object?[] { Day(statement.From), "Debt at the start", statement.Customer.Name, null, null, null, null, statement.OpeningDebt },
        };
        sheetRows.AddRange(statement.Entries.Select(e => (IReadOnlyList<object?>)
        [
            Day(e.Date), KindText(e.Kind), e.Code, e.InvoiceCode, e.Method?.ToString(), Status(e), e.Amount, e.DebtAfter,
        ]));
        sheetRows.Add(new object?[] { Day(statement.To), "Debt at the end", statement.Customer.Name, null, null, null, null, statement.ClosingDebt });
        return new SpreadsheetSheet("Statement", ["Date", "Entry", "Code", "Invoice", "Method", "Status", "Amount", "Debt after"], sheetRows);
    }

    private static SpreadsheetSheet UnpaidSheet(CustomerDebtStatement statement) => new(
        "Unpaid invoices",
        ["Invoice", "Invoice date", "Total", "Covered", "Unpaid", "Age (days)"],
        statement.UnpaidInvoices.Select(i => (IReadOnlyList<object?>)[i.Code, Day(i.InvoiceDate), i.Total, i.Covered, i.Unpaid, i.AgeDays]).ToList());

    private static DateTime? Day(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue);

    private static string? Status(CustomerStatementEntry e) => e.Cancelled ? "Cancelled" : e.AwaitingConfirmation ? "Awaiting confirmation" : null;

    private static string KindText(CustomerStatementEntryKind kind) => kind switch
    {
        CustomerStatementEntryKind.Invoice => "Sales invoice",
        CustomerStatementEntryKind.Return => "Sales return",
        CustomerStatementEntryKind.Payment => "Payment",
        _ => "Money paid back",
    };
}
