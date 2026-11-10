using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.CashBook;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The cash book read side: the self-made vouchers and the supplier payments with money are projected to one shape
/// (<see cref="CashBookEntry"/>) and joined in one query, so the filters, the order, the paging and the sums all run in SQL over both.
/// Enums are projected as their names (strings), the same in both halves of the union. A supplier payment is a Cash line unless it
/// is a bank transfer; a payment is money out, money paid back by the supplier money in.
/// </summary>
internal sealed class CashBookReader(OmtsDbContext db) : ICashBookQuery
{
    public async Task<(IReadOnlyList<CashBookEntry> Items, int Total)> PageAsync(
        CashBookQuery query, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matches = Filter(Entries(), query);
        var total = await matches.CountAsync(cancellationToken);
        var items = await Order(matches, query).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<CashBookEntry>> ListAsync(CashBookQuery query, CancellationToken cancellationToken) =>
        await Order(Filter(Entries(), query), query).ToListAsync(cancellationToken);

    public async Task<CashBookSums> SumsAsync(
        CashFundFilter fund, Guid? cashAccountId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var recorded = InFund(Entries(), fund, cashAccountId).Where(e => !e.Cancelled);
        var before = from is { } start ? await TotalsAsync(recorded.Where(e => e.Date < start), cancellationToken) : (Receipts: 0m, Payments: 0m);
        var within = recorded;
        if (from is { } periodStart)
        {
            within = within.Where(e => e.Date >= periodStart);
        }

        if (to is { } periodEnd)
        {
            within = within.Where(e => e.Date <= periodEnd);
        }

        var inPeriod = await TotalsAsync(within, cancellationToken);
        return new CashBookSums(before.Receipts, before.Payments, inPeriod.Receipts, inPeriod.Payments);
    }

    public async Task<IReadOnlyList<CashPartyView>> SearchPartiesAsync(CashPartyKind kind, string? search, int take, CancellationToken cancellationToken)
    {
        var text = search ?? string.Empty;
        switch (kind)
        {
            case CashPartyKind.Customer:
                return (await db.Customers.AsNoTracking()
                    .Where(c => c.MergedIntoCustomerId == null && (text == "" || c.Name.Contains(text) || (c.PhoneNumber != null && c.PhoneNumber.Contains(text))))
                    .OrderBy(c => c.Name).Take(take)
                    .Select(c => new { c.Id, c.Name, c.PhoneNumber }).ToListAsync(cancellationToken))
                    .Select(c => new CashPartyView(c.Id, kind, null, c.Name, c.PhoneNumber)).ToList();
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
            foreach (var c in await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).Select(c => new { c.Id, c.Name, c.PhoneNumber }).ToListAsync(cancellationToken))
            {
                found[(CashPartyKind.Customer, c.Id)] = new CashPartyView(c.Id, CashPartyKind.Customer, null, c.Name, c.PhoneNumber);
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

    /// <summary>
    /// Every voucher and every supplier payment with money (amount above zero) as a line. The supplier payment of a cancelled status is
    /// a Cancelled line; its type is the system type of its kind and it never counts in the business result by itself.
    /// </summary>
    private IQueryable<CashBookEntry> Entries()
    {
        var vouchers = db.CashVouchers.AsNoTracking().Select(v => new CashBookEntry
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
        var payments = db.SupplierPayments.AsNoTracking().Where(p => p.Amount > 0).Select(p => new CashBookEntry
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
        return vouchers.Concat(payments);
    }

    /// <summary>The lines of one fund, and of one account when named.</summary>
    private static IQueryable<CashBookEntry> InFund(IQueryable<CashBookEntry> entries, CashFundFilter fund, Guid? cashAccountId)
    {
        if (fund != CashFundFilter.All)
        {
            var name = fund.ToString();
            entries = entries.Where(e => e.Fund == name);
        }

        return cashAccountId is { } accountId ? entries.Where(e => e.CashAccountId == accountId) : entries;
    }

    private static IQueryable<CashBookEntry> Filter(IQueryable<CashBookEntry> entries, CashBookQuery query)
    {
        entries = InFund(entries, query.Fund, query.CashAccountId);
        if (query.From is { } from)
        {
            entries = entries.Where(e => e.Date >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(e => e.Date <= to);
        }

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

    private static async Task<(decimal Receipts, decimal Payments)> TotalsAsync(IQueryable<CashBookEntry> entries, CancellationToken cancellationToken)
    {
        var sums = await entries.GroupBy(e => e.Direction).Select(g => new { Direction = g.Key, Sum = g.Sum(e => e.Amount) }).ToListAsync(cancellationToken);
        return (sums.FirstOrDefault(s => s.Direction == "Receipt")?.Sum ?? 0m, sums.FirstOrDefault(s => s.Direction == "Payment")?.Sum ?? 0m);
    }
}
