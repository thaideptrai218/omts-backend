using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.CashBook;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The cash book read side: the self-made vouchers, the supplier payments with money and the customer payments that moved money are projected to one shape
/// (<see cref="CashBookEntry"/>) and joined in one query, so the filters, the order, the paging and the sums all run in SQL over all of them.
/// Enums are projected as their names (strings), the same in both halves of the union. A supplier payment is a Cash line unless it
/// is a bank transfer; a payment is money out, money paid back by the supplier money in.
/// </summary>
internal sealed class CashBookReader(OmtsDbContext db) : ICashBookQuery
{
    public async Task<(IReadOnlyList<CashBookEntry> Items, int Total)> PageAsync(
        CashBookQuery query, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matches = Entries(query);
        var total = await matches.CountAsync(cancellationToken);
        var items = await Order(matches, query).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<CashBookEntry>> ListAsync(CashBookQuery query, CancellationToken cancellationToken) =>
        await Order(Entries(query), query).ToListAsync(cancellationToken);

    public async Task<CashBookSums> SumsAsync(
        CashFundFilter fund, Guid? cashAccountId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        // One read over the three tables, each cut to the fund, the account and the end of the period on its own columns: the rows before the period and the rows in it are told apart and
        // added up by the database, so the history before the period is never read row by row. No lower bound: the opening balance is everything before the period.
        var upper = to is { } end && from is { } start && start > end ? start.AddDays(-1) : to;
        var lines = VoucherSource(fund, cashAccountId, null, upper).Where(v => v.CancelledAt == null)
            .Select(v => new MoneyLine { Date = v.VoucherDate, Receipt = v.Direction == CashDirection.Receipt, Amount = v.Amount })
            .Concat(SupplierPaymentSource(fund, cashAccountId, null, upper).Where(p => p.StatusId != StatusIds.SupplierPayment.Cancelled)
                .Select(p => new MoneyLine { Date = p.PaymentDate, Receipt = p.Kind != SupplierPaymentKind.Payment, Amount = p.Amount }))
            .Concat(CustomerPaymentSource(fund, cashAccountId, null, upper).Where(p => p.StatusId != StatusIds.CustomerPayment.Cancelled)
                .Select(p => new MoneyLine { Date = p.PaymentDate, Receipt = p.Kind == CustomerPaymentKind.Payment, Amount = p.Amount }));
        var periodStart = from ?? DateOnly.MinValue;
        var sums = await lines.GroupBy(l => new { Before = l.Date < periodStart, l.Receipt })
            .Select(g => new { g.Key.Before, g.Key.Receipt, Sum = g.Sum(l => l.Amount) }).ToListAsync(cancellationToken);
        decimal SumOf(bool before, bool receipt) => sums.Where(s => s.Before == before && s.Receipt == receipt).Sum(s => s.Sum);
        return new CashBookSums(SumOf(true, true), SumOf(true, false), SumOf(false, true), SumOf(false, false));
    }

    public async Task<IReadOnlyList<CashPartyView>> SearchPartiesAsync(CashPartyKind kind, string? search, int take, CancellationToken cancellationToken)
    {
        var text = search ?? string.Empty;
        switch (kind)
        {
            case CashPartyKind.Customer:
                return (await db.Customers.AsNoTracking()
                    .Where(c => c.MergedIntoCustomerId == null && (text == "" || c.Name.Contains(text) || c.Code.Contains(text) || (c.PhoneNumber != null && c.PhoneNumber.Contains(text))))
                    .OrderBy(c => c.Name).Take(take)
                    .Select(c => new { c.Id, c.Code, c.Name, c.PhoneNumber }).ToListAsync(cancellationToken))
                    .Select(c => new CashPartyView(c.Id, kind, c.Code, c.Name, c.PhoneNumber)).ToList();
            case CashPartyKind.Supplier:
                return (await db.Suppliers.AsNoTracking()
                    .Where(s => s.StatusId == StatusIds.Usage.InUse && (text == "" || s.Name.Contains(text) || s.PhoneNumber.Contains(text) || s.Code.Contains(text)))
                    .OrderBy(s => s.Name).Take(take)
                    .Select(s => new { s.Id, s.Code, s.Name, s.PhoneNumber }).ToListAsync(cancellationToken))
                    .Select(s => new CashPartyView(s.Id, kind, s.Code, s.Name, s.PhoneNumber)).ToList();
            case CashPartyKind.Employee:
                return (await db.Employees.AsNoTracking()
                    .Where(e => text == "" || e.FullName.Contains(text) || (e.PhoneNumber != null && e.PhoneNumber.Contains(text)))
                    .OrderBy(e => e.FullName).Take(take)
                    .Select(e => new { e.Id, e.FullName, e.PhoneNumber }).ToListAsync(cancellationToken))
                    .Select(e => new CashPartyView(e.Id, kind, null, e.FullName, e.PhoneNumber)).ToList();
            default:
                return (await db.CashParties.AsNoTracking()
                    .Where(p => text == "" || p.Name.Contains(text) || (p.PhoneNumber != null && p.PhoneNumber.Contains(text)))
                    .OrderBy(p => p.Name).Take(take)
                    .Select(p => new { p.Id, p.Name, p.PhoneNumber }).ToListAsync(cancellationToken))
                    .Select(p => new CashPartyView(p.Id, kind, null, p.Name, p.PhoneNumber)).ToList();
        }
    }

    public async Task<IReadOnlyDictionary<(CashPartyKind Kind, Guid Id), CashPartyView>> PartiesAsync(
        IReadOnlyCollection<(CashPartyKind Kind, Guid Id)> keys, CancellationToken cancellationToken)
    {
        var found = new Dictionary<(CashPartyKind, Guid), CashPartyView>();
        IReadOnlyList<Guid> IdsOf(CashPartyKind kind) => keys.Where(k => k.Kind == kind).Select(k => k.Id).ToList();

        if (IdsOf(CashPartyKind.Customer) is { Count: > 0 } customerIds)
        {
            foreach (var c in await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).Select(c => new { c.Id, c.Code, c.Name, c.PhoneNumber }).ToListAsync(cancellationToken))
            {
                found[(CashPartyKind.Customer, c.Id)] = new CashPartyView(c.Id, CashPartyKind.Customer, c.Code, c.Name, c.PhoneNumber);
            }
        }

        if (IdsOf(CashPartyKind.Supplier) is { Count: > 0 } supplierIds)
        {
            foreach (var s in await db.Suppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id)).Select(s => new { s.Id, s.Code, s.Name, s.PhoneNumber }).ToListAsync(cancellationToken))
            {
                found[(CashPartyKind.Supplier, s.Id)] = new CashPartyView(s.Id, CashPartyKind.Supplier, s.Code, s.Name, s.PhoneNumber);
            }
        }

        if (IdsOf(CashPartyKind.Employee) is { Count: > 0 } employeeIds)
        {
            foreach (var e in await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).Select(e => new { e.Id, e.FullName, e.PhoneNumber }).ToListAsync(cancellationToken))
            {
                found[(CashPartyKind.Employee, e.Id)] = new CashPartyView(e.Id, CashPartyKind.Employee, null, e.FullName, e.PhoneNumber);
            }
        }

        if (IdsOf(CashPartyKind.Other) is { Count: > 0 } otherIds)
        {
            foreach (var p in await db.CashParties.AsNoTracking().Where(p => otherIds.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.PhoneNumber }).ToListAsync(cancellationToken))
            {
                found[(CashPartyKind.Other, p.Id)] = new CashPartyView(p.Id, CashPartyKind.Other, null, p.Name, p.PhoneNumber);
            }
        }

        return found;
    }

    /// <summary>One line of money for the sums: when, which way, how much.</summary>
    private sealed class MoneyLine
    {
        public DateOnly Date { get; init; }
        public bool Receipt { get; init; }
        public decimal Amount { get; init; }
    }

    /// <summary>
    /// Every voucher, every supplier payment with money (amount above zero) and every customer payment that moved money as a line, the lines of the query only. The fund, the account and
    /// the period are cut on the columns of each table before the lines are joined into one list (an index can serve them); the other filters follow on each part, then the parts are
    /// concatenated. The supplier payment of a cancelled status is a Cancelled line; its type is the system type of its kind and it never counts in the business result by itself.
    /// </summary>
    private IQueryable<CashBookEntry> Entries(CashBookQuery query)
    {
        var vouchers = VoucherSource(query.Fund, query.CashAccountId, query.From, query.To).Select(v => new CashBookEntry
        {
            Source = "CashVoucher",
            Id = v.Id,
            Code = v.Code,
            OccurredAt = v.OccurredAt,
            Date = v.VoucherDate,
            CreatedAt = v.CreatedAt,
            Direction = v.Direction == CashDirection.Receipt ? "Receipt" : "Payment",
            Fund = v.Fund == CashFund.Cash ? "Cash" : v.Fund == CashFund.Bank ? "Bank" : "EWallet",
            CashAccountId = v.CashAccountId,
            TypeId = v.TypeId,
            PartyKind = v.PartyKind == CashPartyKind.Customer ? "Customer"
                : v.PartyKind == CashPartyKind.Supplier ? "Supplier"
                : v.PartyKind == CashPartyKind.Employee ? "Employee"
                : v.PartyKind == CashPartyKind.Other ? "Other" : null,
            PartyId = v.PartyId,
            PartyName = v.PartyName,
            Amount = v.Amount,
            CollectorId = v.CollectedByAccountId,
            CreatedById = v.CreatedBy,
            Note = v.Note,
            CountsInResult = v.CountsInResult,
            Cancelled = v.CancelledAt != null,
            SourceDocumentKind = null,
            SourceDocumentId = null,
            SourceDocumentCode = null,
        });
        var payments = SupplierPaymentSource(query.Fund, query.CashAccountId, query.From, query.To).Select(p => new CashBookEntry
        {
            Source = p.Kind == SupplierPaymentKind.Payment ? "SupplierPayment" : "SupplierPaidBack",
            Id = p.Id,
            Code = p.Code,
            OccurredAt = p.PaidAt,
            Date = p.PaymentDate,
            CreatedAt = p.CreatedAt,
            Direction = p.Kind == SupplierPaymentKind.Payment ? "Payment" : "Receipt",
            Fund = p.Method == PaymentMethod.BankTransfer ? "Bank" : "Cash",
            CashAccountId = p.CashAccountId,
            TypeId = p.Kind == SupplierPaymentKind.Payment ? CashVoucherTypeIds.PaymentToSupplier : CashVoucherTypeIds.ReceiptSupplierPaidBack,
            PartyKind = "Supplier",
            PartyId = p.SupplierId,
            PartyName = p.Supplier.Name,
            Amount = p.Amount,
            CollectorId = p.PaidByAccountId,
            CreatedById = p.RecordedByAccountId,
            Note = p.Note,
            CountsInResult = false,
            Cancelled = p.StatusId == StatusIds.SupplierPayment.Cancelled,
            SourceDocumentKind = p.PurchaseReturnId != null ? "PurchaseReturn" : "SupplierPayment",
            SourceDocumentId = p.PurchaseReturnId ?? p.Id,
            SourceDocumentCode = p.PurchaseReturn != null ? p.PurchaseReturn.Code : p.Code,
        });
        var customerPayments = CustomerPaymentSource(query.Fund, query.CashAccountId, query.From, query.To).Select(p => new CashBookEntry
        {
            Source = p.Kind == CustomerPaymentKind.Payment ? "CustomerPayment" : "CustomerPaidBack",
            Id = p.Id,
            Code = p.Code,
            OccurredAt = p.PaidAt,
            Date = p.PaymentDate,
            CreatedAt = p.CreatedAt,
            Direction = p.Kind == CustomerPaymentKind.Payment ? "Receipt" : "Payment",
            Fund = p.Method == CustomerPaymentMethod.Cash ? "Cash" : p.Method == CustomerPaymentMethod.EWallet ? "EWallet" : "Bank",
            CashAccountId = p.CashAccountId,
            TypeId = p.Kind == CustomerPaymentKind.Payment ? CashVoucherTypeIds.ReceiptFromCustomer : CashVoucherTypeIds.PaymentToCustomer,
            PartyKind = p.CustomerId != null ? "Customer" : null,
            PartyId = p.CustomerId,
            PartyName = p.Customer != null ? p.Customer.Name : null,
            Amount = p.Amount,
            CollectorId = p.CollectedByAccountId,
            CreatedById = p.RecordedByAccountId,
            Note = p.Note,
            CountsInResult = false,
            Cancelled = p.StatusId == StatusIds.CustomerPayment.Cancelled,
            SourceDocumentKind = "CustomerPayment",
            SourceDocumentId = p.Id,
            SourceDocumentCode = p.Code,
        });
        return Filter(vouchers, query).Concat(Filter(payments, query)).Concat(Filter(customerPayments, query));
    }

    /// <summary>The vouchers of one fund (all of them for All), of one account when named, dated within the period; no bound when a date is null.</summary>
    private IQueryable<CashVoucher> VoucherSource(CashFundFilter fund, Guid? cashAccountId, DateOnly? from, DateOnly? to)
    {
        var vouchers = db.CashVouchers.AsNoTracking();
        vouchers = fund switch
        {
            CashFundFilter.Cash => vouchers.Where(v => v.Fund == CashFund.Cash),
            CashFundFilter.Bank => vouchers.Where(v => v.Fund == CashFund.Bank),
            CashFundFilter.EWallet => vouchers.Where(v => v.Fund == CashFund.EWallet),
            _ => vouchers,
        };
        if (cashAccountId is { } accountId)
        {
            vouchers = vouchers.Where(v => v.CashAccountId == accountId);
        }

        if (from is { } start)
        {
            vouchers = vouchers.Where(v => v.VoucherDate >= start);
        }

        return to is { } end ? vouchers.Where(v => v.VoucherDate <= end) : vouchers;
    }

    /// <summary>
    /// The supplier payments with money, of one fund, one account and the period. A payment is a Cash line unless it is a bank transfer, and never an e-wallet one. Cancelled ones stay: the
    /// callers that do not list them leave them out.
    /// </summary>
    private IQueryable<SupplierPayment> SupplierPaymentSource(CashFundFilter fund, Guid? cashAccountId, DateOnly? from, DateOnly? to)
    {
        var payments = db.SupplierPayments.AsNoTracking().Where(p => p.Amount > 0);
        payments = fund switch
        {
            CashFundFilter.Cash => payments.Where(p => p.Method != PaymentMethod.BankTransfer),
            CashFundFilter.Bank => payments.Where(p => p.Method == PaymentMethod.BankTransfer),
            CashFundFilter.EWallet => payments.Where(p => false),
            _ => payments,
        };
        if (cashAccountId is { } accountId)
        {
            payments = payments.Where(p => p.CashAccountId == accountId);
        }

        if (from is { } start)
        {
            payments = payments.Where(p => p.PaymentDate >= start);
        }

        return to is { } end ? payments.Where(p => p.PaymentDate <= end) : payments;
    }

    /// <summary>
    /// The customer payments that moved money (Completed) and the ones cancelled after that (they were confirmed once), of one fund, one account and the period: a payment waiting for its
    /// transfer, one Awaiting confirmation and one that is all discount are no line. Cash is the Cash fund, an e-wallet the EWallet fund, any transfer the Bank fund.
    /// </summary>
    private IQueryable<CustomerPayment> CustomerPaymentSource(CashFundFilter fund, Guid? cashAccountId, DateOnly? from, DateOnly? to)
    {
        var payments = db.CustomerPayments.AsNoTracking()
            .Where(p => p.Amount > 0 && (p.StatusId == StatusIds.CustomerPayment.Completed || (p.StatusId == StatusIds.CustomerPayment.Cancelled && p.ConfirmedAt != null)));
        payments = fund switch
        {
            CashFundFilter.Cash => payments.Where(p => p.Method == CustomerPaymentMethod.Cash),
            CashFundFilter.Bank => payments.Where(p => p.Method != CustomerPaymentMethod.Cash && p.Method != CustomerPaymentMethod.EWallet),
            CashFundFilter.EWallet => payments.Where(p => p.Method == CustomerPaymentMethod.EWallet),
            _ => payments,
        };
        if (cashAccountId is { } accountId)
        {
            payments = payments.Where(p => p.CashAccountId == accountId);
        }

        if (from is { } start)
        {
            payments = payments.Where(p => p.PaymentDate >= start);
        }

        return to is { } end ? payments.Where(p => p.PaymentDate <= end) : payments;
    }

    /// <summary>The filters of the query other than the fund, the account and the period (those are cut on the tables, see <see cref="Entries"/>), applied to one part of the list.</summary>
    private static IQueryable<CashBookEntry> Filter(IQueryable<CashBookEntry> entries, CashBookQuery query)
    {
        if (query.Direction is { } direction)
        {
            var name = direction.ToString();
            entries = entries.Where(e => e.Direction == name);
        }

        if (query.TypeId is { } typeId)
        {
            entries = entries.Where(e => e.TypeId == typeId);
        }

        if (query.Status is { } status)
        {
            var cancelled = status == CashEntryStatus.Cancelled;
            entries = entries.Where(e => e.Cancelled == cancelled);
        }

        if (query.CreatedById is { } createdById)
        {
            entries = entries.Where(e => e.CreatedById == createdById);
        }

        if (query.PartyKind is { } partyKind)
        {
            var name = partyKind.ToString();
            entries = entries.Where(e => e.PartyKind == name);
        }

        if (!string.IsNullOrWhiteSpace(query.Party))
        {
            var party = query.Party.Trim();
            entries = entries.Where(e => e.PartyName != null && e.PartyName.Contains(party));
        }

        if (query.CountsInResult is { } countsInResult)
        {
            entries = entries.Where(e => e.CountsInResult == countsInResult);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var code = query.Search.Trim();
            entries = entries.Where(e => e.Code.Contains(code));
        }

        return entries;
    }

    /// <summary>By code or by time as asked, then by the other of the two and the id, so a page never repeats or skips a line.</summary>
    private static IQueryable<CashBookEntry> Order(IQueryable<CashBookEntry> entries, CashBookQuery query) => (query.Sort, query.Desc) switch
    {
        (CashBookSort.Time, true) => entries.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Code).ThenBy(e => e.Id),
        (CashBookSort.Time, false) => entries.OrderBy(e => e.OccurredAt).ThenBy(e => e.Code).ThenBy(e => e.Id),
        (_, true) => entries.OrderByDescending(e => e.Code).ThenByDescending(e => e.OccurredAt).ThenBy(e => e.Id),
        _ => entries.OrderBy(e => e.Code).ThenBy(e => e.OccurredAt).ThenBy(e => e.Id),
    };
}
