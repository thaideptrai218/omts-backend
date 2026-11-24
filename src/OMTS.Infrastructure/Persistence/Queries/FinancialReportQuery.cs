using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The money of the business result: sums per day (or per month for payrolls) read in the database and cut into columns by the caller. Sales return values are put on the date of their invoice,
/// as the sales report counts them, so the net revenue of this report matches it. Read only.
/// </summary>
internal sealed class FinancialReportQuery(OmtsDbContext db) : IFinancialReportQuery
{
    /// <summary>The Windows name of the zone of Vietnam (UTC+7, no daylight saving) that SQL Server and Azure SQL know for AT TIME ZONE; it is the business time of <see cref="BusinessClock"/>.</summary>
    private const string VietnamTimeZone = "SE Asia Standard Time";

    public async Task<IReadOnlyList<FinancialEntry>> EntriesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var entries = new List<FinancialEntry>();
        var invoices = db.SalesInvoices.AsNoTracking().Where(i => i.StatusId == StatusIds.SalesInvoice.Completed && i.InvoiceDate >= from && i.InvoiceDate <= to);
        entries.AddRange((await invoices.GroupBy(i => i.InvoiceDate)
                .Select(g => new { Date = g.Key, Revenue = g.Sum(i => i.Subtotal + i.OtherCharges), Discounts = g.Sum(i => i.DiscountAmount), Cuts = g.Sum(i => i.PriceCutTotal) })
                .ToListAsync(cancellationToken))
            .SelectMany(d => new[]
            {
                new FinancialEntry(FinancialEntryCodes.Revenue, d.Date, d.Revenue),
                new FinancialEntry(FinancialEntryCodes.InvoiceDiscounts, d.Date, d.Discounts),
                new FinancialEntry(FinancialEntryCodes.PriceCuts, d.Date, d.Cuts),
            }));
        entries.AddRange((await db.SalesReturns.AsNoTracking()
                .Where(r => r.StatusId == StatusIds.SalesReturn.Recorded && r.Invoice.StatusId == StatusIds.SalesInvoice.Completed && r.Invoice.InvoiceDate >= from && r.Invoice.InvoiceDate <= to)
                .GroupBy(r => r.Invoice.InvoiceDate)
                .Select(g => new { Date = g.Key, Value = g.Sum(r => r.Value) })
                .ToListAsync(cancellationToken))
            .Select(d => new FinancialEntry(FinancialEntryCodes.SalesReturns, d.Date, d.Value)));
        entries.AddRange((await db.CustomerPayments.AsNoTracking()
                .Where(p => p.Kind == CustomerPaymentKind.Payment && p.DiscountAmount > 0m && p.PaymentDate >= from && p.PaymentDate <= to
                    && p.StatusId != StatusIds.CustomerPayment.Cancelled && p.StatusId != StatusIds.CustomerPayment.PendingTransfer)
                .GroupBy(p => p.PaymentDate)
                .Select(g => new { Date = g.Key, Value = g.Sum(p => p.DiscountAmount) })
                .ToListAsync(cancellationToken))
            .Select(d => new FinancialEntry(FinancialEntryCodes.PaymentDiscounts, d.Date, d.Value)));

        // The business date of an issue is the date of its instant at UTC+7, whatever offset it was stored with: the database moves the instant to the Vietnam time zone (UTC+7, no daylight
        // saving) and cuts the date, so the lines are added up per kind and day there instead of being read one by one.
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var endExclusive = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var issueSums = await db.InputIssues.AsNoTracking()
            .Where(i => i.StatusId == StatusIds.InputIssue.Completed && i.IssuedAt >= start && i.IssuedAt < endExclusive)
            .SelectMany(i => i.Lines, (i, l) => new { i.Kind, Day = EF.Functions.AtTimeZone(i.IssuedAt, VietnamTimeZone).Date, l.Value })
            .GroupBy(x => new { x.Kind, x.Day })
            .Select(g => new { g.Key.Kind, g.Key.Day, Value = g.Sum(x => x.Value) })
            .ToListAsync(cancellationToken);
        entries.AddRange(issueSums.Select(d => new FinancialEntry(
            d.Kind == InputIssueKind.Use ? FinancialEntryCodes.InputUseCost : FinancialEntryCodes.InputDisposalCost, DateOnly.FromDateTime(d.Day), d.Value)));

        // Wages count in the expense month chosen when the sheet was finalized: whole months, from the month of the first day to that of the last. The cost is the gross income of the payslips
        // (main pay + bonus): an advance already paid is a receivable settled against the payslip, not a cost that disappears (decision of 19/11/2026, REP-01).
        var (firstMonth, lastMonth) = ($"{from:yyyy-MM}", $"{to:yyyy-MM}");
        // The expense month is always yyyy-MM (PayrollRules), so the text order is the calendar order in any collation.
        var payrolls = await db.Payrolls.AsNoTracking()
            .Where(p => p.StatusId == StatusIds.Payroll.Finalized && p.ExpenseMonth != null && p.ExpenseMonth.CompareTo(firstMonth) >= 0 && p.ExpenseMonth.CompareTo(lastMonth) <= 0)
            .SelectMany(p => p.Lines, (p, l) => new { p.ExpenseMonth, l.MainPay, l.Bonus })
            .GroupBy(x => x.ExpenseMonth!)
            .Select(g => new { Month = g.Key, Net = g.Sum(x => x.MainPay + x.Bonus) })
            .ToListAsync(cancellationToken);
        entries.AddRange(payrolls.Select(p => new FinancialEntry(FinancialEntryCodes.LaborCost, DateOnly.ParseExact($"{p.Month}-01", "yyyy-MM-dd"), p.Net)));

        // Other expenses and other income: the vouchers that count in the result, of those two categories only (goods and wages have their own lines; a transfer is no result).
        entries.AddRange((await db.CashVouchers.AsNoTracking()
                .Where(v => v.CancelledAt == null && v.CountsInResult && v.VoucherDate >= from && v.VoucherDate <= to
                    && (v.Type.Category == CashCategory.OtherExpense || v.Type.Category == CashCategory.OtherIncome))
                .GroupBy(v => new { v.VoucherDate, v.Type.Category })
                .Select(g => new { g.Key.VoucherDate, g.Key.Category, Amount = g.Sum(v => v.Amount) })
                .ToListAsync(cancellationToken))
            .Select(d => new FinancialEntry(d.Category == CashCategory.OtherExpense ? FinancialEntryCodes.OtherExpense : FinancialEntryCodes.OtherIncome, d.VoucherDate, d.Amount)));
        return entries;
    }
}
