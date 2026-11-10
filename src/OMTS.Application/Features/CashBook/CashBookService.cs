using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.CashBook;

/// <summary>
/// The cash book: the self-made vouchers and the supplier payments with money (an amount above zero), read as one list of lines
/// with filters, paging and the balance of the fund. Cancelled lines are listed when asked for but never counted. The API checks
/// the role: no zone, the Owner and the Accountant.
/// </summary>
public sealed class CashBookService(ICashBookQuery book, CashBookLookupService lookups)
{
    public const int MaxPageSize = 100;

    public async Task<Result<CashBookPage>> SearchAsync(CashBookQuery query, CancellationToken cancellationToken)
    {
        if (PeriodError(query) is { } invalid)
        {
            return invalid;
        }

        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var (items, total) = await book.PageAsync(query, page, pageSize, cancellationToken);
        var read = await lookups.LoadAsync(items, cancellationToken);
        return new CashBookPage(items.Select(read.Row).ToList(), page, pageSize, total, await SummaryAsync(query, cancellationToken));
    }

    /// <summary>Every line of the query, with the references they name read (the export).</summary>
    internal async Task<(IReadOnlyList<CashBookRow> Rows, CashBookSummary Summary)> AllAsync(CashBookQuery query, CancellationToken cancellationToken)
    {
        var items = await book.ListAsync(query, cancellationToken);
        var read = await lookups.LoadAsync(items, cancellationToken);
        return (items.Select(read.Row).ToList(), await SummaryAsync(query, cancellationToken));
    }

    /// <summary>The balance of the fund (and account) over the period only: the other filters do not change it.</summary>
    private async Task<CashBookSummary> SummaryAsync(CashBookQuery query, CancellationToken cancellationToken)
    {
        var sums = await book.SumsAsync(query.Fund, query.CashAccountId, query.From, query.To, cancellationToken);
        return CashBookSummary.Of(sums.ReceiptsBefore, sums.PaymentsBefore, sums.Receipts, sums.Payments);
    }

    /// <summary>MSG-GEN-01 when the period starts after it ends or is longer than a list may read.</summary>
    internal static AppError? PeriodError(CashBookQuery query) =>
        query is { From: { } from, To: { } to } && PurchasePeriod.Check(from, to) is { IsSuccess: false } wrong ? wrong.Error : null;
}
