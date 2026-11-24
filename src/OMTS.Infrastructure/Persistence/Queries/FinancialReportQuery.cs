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

        // Issue times are stored at UTC+7; the business date is read here, from the time, so no time zone rule has to be translated.
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var endExclusive = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var issueLines = await db.InputIssues.AsNoTracking()
            .Where(i => i.StatusId == StatusIds.InputIssue.Completed && i.IssuedAt >= start && i.IssuedAt < endExclusive)
            .SelectMany(i => i.Lines.Select(l => new { i.Kind, i.IssuedAt, l.Value }))
            .ToListAsync(cancellationToken);
        entries.AddRange(issueLines
            .GroupBy(l => (l.Kind, Date: BusinessClock.ToBusinessDate(l.IssuedAt)))
            .Select(g => new FinancialEntry(g.Key.Kind == InputIssueKind.Use ? FinancialEntryCodes.InputUseCost : FinancialEntryCodes.InputDisposalCost, g.Key.Date, g.Sum(l => l.Value))));

        // Wages count in the expense month chosen when the sheet was finalized: whole months, from the month of the first day to that of the last. The cost is the gross income of the payslips
        // (main pay + bonus): an advance already paid is a receivable settled against the payslip, not a cost that disappears (decision of 19/11/2026, REP-01).
        var (firstMonth, lastMonth) = ($"{from:yyyy-MM}", $"{to:yyyy-MM}");
        var payrolls = await db.Payrolls.AsNoTracking()
            .Where(p => p.StatusId == StatusIds.Payroll.Finalized && p.ExpenseMonth != null)
            .Select(p => new { p.ExpenseMonth, Net = p.Lines.Sum(l => l.MainPay + l.Bonus) })
            .ToListAsync(cancellationToken);
        entries.AddRange(payrolls
            .Where(p => string.CompareOrdinal(p.ExpenseMonth, firstMonth) >= 0 && string.CompareOrdinal(p.ExpenseMonth, lastMonth) <= 0)
            .GroupBy(p => p.ExpenseMonth!)
            .Select(g => new FinancialEntry(FinancialEntryCodes.LaborCost, DateOnly.ParseExact($"{g.Key}-01", "yyyy-MM-dd"), g.Sum(p => p.Net))));

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
