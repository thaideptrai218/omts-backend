using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>The lines the cash book list shows for the same filters, with the balance of the period, as an .xlsx file.</summary>
public sealed class CashBookExportService(CashBookService book, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    private static readonly string[] Headers =
    [
        "Mã phiếu", "Thời gian", "Quỹ", "Tài khoản", "Loại thu chi", "Đối tượng", "Người nộp/nhận", "Người thu/chi", "Người tạo",
        "Thu", "Chi", "Tính vào kết quả", "Trạng thái", "Ghi chú",
    ];

    public async Task<Result<ExportedFile>> ExportAsync(CashBookQuery query, CancellationToken cancellationToken)
    {
        if (CashBookService.PeriodError(query) is { } invalid)
        {
            return invalid;
        }

        var (rows, summary) = await book.AllAsync(query, cancellationToken);
        var sheetRows = rows.Select(r => (IReadOnlyList<object?>)
        [
            r.Code, r.OccurredAt.ToOffset(BusinessClock.UtcOffset).DateTime, FundText(r.Fund), r.CashAccount is { } a ? $"{a.ProviderName} {a.AccountNumber}" : null,
            r.Type.Name, r.Party is { } p ? KindText(p.Kind) : null, r.Party?.Name, r.Collector?.Name, r.CreatedBy?.Name,
            r.Direction == CashDirection.Receipt ? r.Amount : null, r.Direction == CashDirection.Payment ? r.Amount : null,
            r.CountsInResult ? "Có" : "Không", r.Status == CashEntryStatus.Cancelled ? "Đã hủy" : "Đã ghi", r.Note,
        ]).ToList();
        sheetRows.Add(Total("Tồn đầu kỳ", summary.Opening));
        sheetRows.Add(Total("Tổng thu", summary.TotalIn));
        sheetRows.Add(Total("Tổng chi", summary.TotalOut));
        sheetRows.Add(Total("Tồn cuối kỳ", summary.Closing));
        try
        {
            var content = spreadsheets.Write([new SpreadsheetSheet("Sổ quỹ", Headers, sheetRows)]);
            return new ExportedFile($"cash-book-{timeProvider.Today():yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, content);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private static IReadOnlyList<object?> Total(string label, decimal amount) =>
        [label, null, null, null, null, null, null, null, null, amount, null, null, null, null];

    private static string FundText(CashFund fund) => fund switch
    {
        CashFund.Cash => "Tiền mặt",
        CashFund.Bank => "Ngân hàng",
        _ => "Ví điện tử",
    };

    private static string KindText(CashPartyKind kind) => kind switch
    {
        CashPartyKind.Customer => "Khách hàng",
        CashPartyKind.Supplier => "Nhà cung cấp",
        CashPartyKind.Employee => "Nhân viên",
        _ => "Khác",
    };
}
