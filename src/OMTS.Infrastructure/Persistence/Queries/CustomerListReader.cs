using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The customer list: the profiles that are not merged, with the figures the columns and the filters read, all summed by SQL so the debt is the one every other screen shows
/// (invoices not cancelled − payments received and their discounts, not cancelled and not waiting for their transfer, + money paid back + debt adjustments not cancelled).
/// The sales are the invoices not cancelled in the period (all time without one): the total before the price cuts, and the total after them.
/// </summary>
internal sealed class CustomerListReader(OmtsDbContext db) : ICustomerListQuery
{
    private sealed class Figures
    {
        public Customer Customer { get; init; } = null!;
        public decimal TotalSales { get; init; }
        public decimal NetSales { get; init; }
        public decimal Debt { get; init; }
        public DateTimeOffset? LastTransactionAt { get; init; }
    }

    public async Task<(IReadOnlyList<CustomerFigureRow> Items, int Total, CustomerListSums Sums)> SearchAsync(
        CustomerSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var rows = await Sorted(query, filter).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        // The sums of every match: SQL Server cannot sum an expression that holds a subquery, so the three figures of each match are read and added up here.
        var figures = total == 0 ? [] : await query.Select(f => new { f.Debt, f.TotalSales, f.NetSales }).ToListAsync(cancellationToken);
        return (rows.Select(Row).ToList(), total, new CustomerListSums(figures.Sum(f => f.Debt), figures.Sum(f => f.TotalSales), figures.Sum(f => f.NetSales)));
    }

    public async Task<IReadOnlyList<CustomerFigureRow>> ListAsync(CustomerSearchFilter filter, CancellationToken cancellationToken) =>
        (await Sorted(Filtered(filter), filter).ToListAsync(cancellationToken)).Select(Row).ToList();

    private IQueryable<Figures> Filtered(CustomerSearchFilter filter)
    {
        var customers = db.Customers.AsNoTracking().Where(c => c.MergedIntoCustomerId == null);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            var phone = filter.PhoneSearch;
            customers = customers.Where(c => c.Name.Contains(text) || c.Code.Contains(text)
                || (c.PhoneNumber != null && (c.PhoneNumber.Contains(text) || (phone != null && c.PhoneNumber == phone)))
                || (c.PhoneNumber2 != null && c.PhoneNumber2.Contains(text)));
        }

        if (filter.Type is { } type)
        {
            customers = customers.Where(c => c.CustomerType == type);
        }

        if (filter.Gender is { } gender)
        {
            customers = customers.Where(c => c.Gender == gender);
        }

        if (filter.StatusId is { } statusId)
        {
            customers = customers.Where(c => c.StatusId == statusId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var province = filter.Province.Trim();
            customers = customers.Where(c => c.Province == province);
        }

        if (filter.CreatedById is { } createdBy)
        {
            customers = customers.Where(c => c.CreatedBy == createdBy);
        }

        if (filter.CreatedFrom is { } createdFrom)
        {
            var start = Start(createdFrom);
            customers = customers.Where(c => c.CreatedAt >= start);
        }

        if (filter.CreatedTo is { } createdTo)
        {
            var end = Start(createdTo.AddDays(1));
            customers = customers.Where(c => c.CreatedAt < end);
        }

        if (filter.BirthdayFrom is { } birthdayFrom && filter.BirthdayTo is { } birthdayTo)
        {
            var from = birthdayFrom.Month * 100 + birthdayFrom.Day;
            var to = birthdayTo.Month * 100 + birthdayTo.Day;
            customers = from <= to
                ? customers.Where(c => c.BirthDate != null && c.BirthDate.Value.Month * 100 + c.BirthDate.Value.Day >= from && c.BirthDate.Value.Month * 100 + c.BirthDate.Value.Day <= to)
                : customers.Where(c => c.BirthDate != null && (c.BirthDate.Value.Month * 100 + c.BirthDate.Value.Day >= from || c.BirthDate.Value.Month * 100 + c.BirthDate.Value.Day <= to));
        }

        var salesFrom = filter.SalesFrom;
        var salesTo = filter.SalesTo;
        var cancelled = StatusIds.SalesInvoice.Cancelled;
        var paymentCancelled = StatusIds.CustomerPayment.Cancelled;
        var paymentPending = StatusIds.CustomerPayment.PendingTransfer;
        var query = customers.Select(c => new Figures
        {
            Customer = c,
            TotalSales = db.SalesInvoices
                .Where(i => i.CustomerId == c.Id && i.StatusId != cancelled && (salesFrom == null || i.InvoiceDate >= salesFrom) && (salesTo == null || i.InvoiceDate <= salesTo))
                .Sum(i => (decimal?)(i.Total + i.PriceCutTotal)) ?? 0m,
            NetSales = db.SalesInvoices
                .Where(i => i.CustomerId == c.Id && i.StatusId != cancelled && (salesFrom == null || i.InvoiceDate >= salesFrom) && (salesTo == null || i.InvoiceDate <= salesTo))
                .Sum(i => (decimal?)i.Total) ?? 0m,
            Debt = (db.SalesInvoices.Where(i => i.CustomerId == c.Id && i.StatusId != cancelled).Sum(i => (decimal?)i.Total) ?? 0m)
                - (db.CustomerPayments
                    .Where(p => p.CustomerId == c.Id && p.Kind == Domain.Enums.CustomerPaymentKind.Payment && p.StatusId != paymentCancelled && p.StatusId != paymentPending)
                    .Sum(p => (decimal?)(p.Amount + p.DiscountAmount)) ?? 0m)
                + (db.CustomerPayments
                    .Where(p => p.CustomerId == c.Id && p.Kind == Domain.Enums.CustomerPaymentKind.PaidBack && p.StatusId != paymentCancelled && p.StatusId != paymentPending)
                    .Sum(p => (decimal?)p.Amount) ?? 0m)
                + (db.CustomerDebtAdjustments.Where(a => a.CustomerId == c.Id && a.CancelledAt == null).Sum(a => (decimal?)a.Amount) ?? 0m),
            LastTransactionAt = db.SalesInvoices.Where(i => i.CustomerId == c.Id && i.StatusId != cancelled).Max(i => (DateTimeOffset?)i.InvoicedAt),
        });

        if (filter.TotalSalesFrom is { } salesMin)
        {
            query = query.Where(f => f.TotalSales >= salesMin);
        }

        if (filter.TotalSalesTo is { } salesMax)
        {
            query = query.Where(f => f.TotalSales <= salesMax);
        }

        if (filter.CurrentDebtFrom is { } debtMin)
        {
            query = query.Where(f => f.Debt >= debtMin);
        }

        if (filter.CurrentDebtTo is { } debtMax)
        {
            query = query.Where(f => f.Debt <= debtMax);
        }

        if (filter.LastTransactionFrom is { } lastFrom)
        {
            var start = Start(lastFrom);
            query = query.Where(f => f.LastTransactionAt != null && f.LastTransactionAt >= start);
        }

        if (filter.LastTransactionTo is { } lastTo)
        {
            var end = Start(lastTo.AddDays(1));
            query = query.Where(f => f.LastTransactionAt != null && f.LastTransactionAt < end);
        }

        return query;
    }

    private static IQueryable<Figures> Sorted(IQueryable<Figures> query, CustomerSearchFilter filter)
    {
        var descending = filter.Descending;
        var sorted = filter.Sort switch
        {
            CustomerSort.Name => descending ? query.OrderByDescending(f => f.Customer.Name) : query.OrderBy(f => f.Customer.Name),
            CustomerSort.Phone => descending ? query.OrderByDescending(f => f.Customer.PhoneNumber) : query.OrderBy(f => f.Customer.PhoneNumber),
            CustomerSort.Type => descending ? query.OrderByDescending(f => f.Customer.CustomerType) : query.OrderBy(f => f.Customer.CustomerType),
            CustomerSort.Province => descending ? query.OrderByDescending(f => f.Customer.Province) : query.OrderBy(f => f.Customer.Province),
            CustomerSort.CreatedAt => descending ? query.OrderByDescending(f => f.Customer.CreatedAt) : query.OrderBy(f => f.Customer.CreatedAt),
            CustomerSort.Status => descending ? query.OrderByDescending(f => f.Customer.StatusId) : query.OrderBy(f => f.Customer.StatusId),
            CustomerSort.TotalSales => descending ? query.OrderByDescending(f => f.TotalSales) : query.OrderBy(f => f.TotalSales),
            CustomerSort.NetSales => descending ? query.OrderByDescending(f => f.NetSales) : query.OrderBy(f => f.NetSales),
            CustomerSort.CurrentDebt => descending ? query.OrderByDescending(f => f.Debt) : query.OrderBy(f => f.Debt),
            CustomerSort.LastTransaction => descending ? query.OrderByDescending(f => f.LastTransactionAt) : query.OrderBy(f => f.LastTransactionAt),
            _ => descending ? query.OrderByDescending(f => f.Customer.Code) : query.OrderBy(f => f.Customer.Code),
        };
        return sorted.ThenBy(f => f.Customer.Name).ThenBy(f => f.Customer.Id);
    }

    private static CustomerFigureRow Row(Figures f) => new(f.Customer, f.TotalSales, f.NetSales, f.Debt, f.LastTransactionAt);

    /// <summary>The start of a business day (UTC+7).</summary>
    private static DateTimeOffset Start(DateOnly day) => BusinessClock.FromLocal(day.ToDateTime(TimeOnly.MinValue));
}
