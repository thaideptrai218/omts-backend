using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>PURCHASE-04.2: the debt list and, when a supplier is chosen, its statement of the period, as an .xlsx file.</summary>
public sealed class SupplierDebtExportService(SupplierDebtService debts, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    public async Task<Result<ExportedFile>> ExportAsync(SupplierDebtExportQuery query, CancellationToken cancellationToken)
    {
        SupplierDebtStatement? statement = null;
        if (query.SupplierId is { } supplierId)
        {
            var found = await debts.StatementAsync(supplierId, query.From, query.To, cancellationToken);
            if (!found.IsSuccess)
            {
                return found.Error!;
            }

            statement = found.Value;
        }
        else if (PurchasePeriod.Resolve(query.From, query.To, timeProvider.Today()) is { IsSuccess: false } wrongPeriod)
        {
            // The list does not use the period, but a wrong period gives no file, as with a supplier.
            return wrongPeriod.Error!;
        }

        var sheets = new List<SpreadsheetSheet> { ListSheet(await debts.RowsAsync(query.Search, cancellationToken)) };
        if (statement is not null)
        {
            sheets.Add(StatementSheet(statement));
        }

        try
        {
            var name = statement is null
                ? $"supplier-debts-{timeProvider.Today():yyyyMMdd}.xlsx"
                : $"supplier-debts-{statement.From:yyyyMMdd}-{statement.To:yyyyMMdd}.xlsx";
            return new ExportedFile(name, ISpreadsheetWriter.ContentType, spreadsheets.Write(sheets));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private static SpreadsheetSheet ListSheet(IReadOnlyList<SupplierDebtRow> rows)
    {
        var sheetRows = rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Supplier.Name, r.Supplier.PhoneNumber, r.Supplier.Status, r.Debt, r.DebtAgeDays, r.OldestUnpaidDate?.ToDateTime(TimeOnly.MinValue),
        ]).ToList();
        sheetRows.Add(["Total owed", null, null, rows.Where(r => r.Debt > 0).Sum(r => r.Debt), null, null]);
        sheetRows.Add(["Total paid in advance", null, null, -rows.Where(r => r.Debt < 0).Sum(r => r.Debt), null, null]);
        return new SpreadsheetSheet("Supplier debts", ["Supplier", "Phone", "Status", "Debt", "Debt age (days)", "Oldest unpaid receipt"], sheetRows);
    }

    private static SpreadsheetSheet StatementSheet(SupplierDebtStatement statement)
    {
        var sheetRows = new List<IReadOnlyList<object?>>
        {
            new object?[] { statement.From.ToDateTime(TimeOnly.MinValue), "Debt at the start", statement.Supplier.Name, null, null, null, null, null, statement.OpeningDebt },
        };
        sheetRows.AddRange(statement.Entries.Select(e => (IReadOnlyList<object?>)
        [
            e.Date.ToDateTime(TimeOnly.MinValue), KindText(e.Kind), e.Code, e.ReceiptCode, e.Method?.ToString(), e.BankReference, e.Note, e.Amount, e.DebtAfter,
        ]));
        sheetRows.Add(new object?[] { statement.To.ToDateTime(TimeOnly.MinValue), "Debt at the end", statement.Supplier.Name, null, null, null, null, null, statement.ClosingDebt });
        return new SpreadsheetSheet(
            "Statement", ["Date", "Entry", "Code", "Receipt", "Method", "Bank reference", "Note", "Amount", "Debt after"], sheetRows);
    }

    private static string KindText(SupplierStatementEntryKind kind) => kind switch
    {
        SupplierStatementEntryKind.Receipt => "Purchase receipt",
        SupplierStatementEntryKind.Return => "Purchase return",
        SupplierStatementEntryKind.ReturnRefund => "Refund on a purchase return",
        SupplierStatementEntryKind.Payment => "Payment",
        _ => "Money paid back",
    };
}
