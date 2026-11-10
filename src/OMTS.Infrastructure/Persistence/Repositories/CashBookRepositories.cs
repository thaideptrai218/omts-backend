using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

// Names are compared upper-cased, as the catalogue compares its codes, so the in-memory test provider answers like SQL Server's
// case-insensitive collation; the unique index of each table still refuses a parallel save of the same value.

internal sealed class CashAccountRepository(OmtsDbContext db) : ICashAccountRepository
{
    public Task<CashAccount?> GetForEditAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.CashAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, CashAccount>> GetByIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        accountIds.Count == 0
            ? new Dictionary<Guid, CashAccount>()
            : await db.CashAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);

    public async Task<IReadOnlyList<CashAccount>> ListAsync(CashAccountKind? kind, bool includeInactive, CancellationToken cancellationToken) =>
        await db.CashAccounts.AsNoTracking()
            .Where(a => (kind == null || a.Kind == kind) && (includeInactive || a.IsActive))
            .OrderBy(a => a.Kind).ThenBy(a => a.ProviderName).ThenBy(a => a.AccountNumber)
            .ToListAsync(cancellationToken);

    public Task<CashAccount?> FindDuplicateAsync(
        CashAccountKind kind, string providerName, string accountNumber, Guid? exceptAccountId, CancellationToken cancellationToken)
    {
        var (provider, number) = (providerName.ToUpperInvariant(), accountNumber.ToUpperInvariant());
        return db.CashAccounts.AsNoTracking().FirstOrDefaultAsync(
            a => a.Kind == kind && a.ProviderName.ToUpper() == provider && a.AccountNumber.ToUpper() == number && a.Id != exceptAccountId, cancellationToken);
    }

    public Task<CashAccount?> FindBankAccountAsync(string bankBin, string accountNumber, CancellationToken cancellationToken)
    {
        var number = accountNumber.ToUpperInvariant();
        return db.CashAccounts.FirstOrDefaultAsync(
            a => a.Kind == CashAccountKind.Bank && a.BankBin == bankBin && a.AccountNumber.ToUpper() == number, cancellationToken);
    }

    public async Task<bool> IsUsedAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.CashVouchers.AnyAsync(v => v.CashAccountId == accountId, cancellationToken)
        || await db.SupplierPayments.AnyAsync(p => p.CashAccountId == accountId, cancellationToken);

    public void Add(CashAccount account) => db.CashAccounts.Add(account);

    public void Remove(CashAccount account) => db.CashAccounts.Remove(account);
}

internal sealed class CashVoucherTypeRepository(OmtsDbContext db) : ICashVoucherTypeRepository
{
    public Task<CashVoucherType?> GetForEditAsync(Guid typeId, CancellationToken cancellationToken) =>
        db.CashVoucherTypes.FirstOrDefaultAsync(t => t.Id == typeId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, CashVoucherType>> GetByIdsAsync(IReadOnlyCollection<Guid> typeIds, CancellationToken cancellationToken) =>
        typeIds.Count == 0
            ? new Dictionary<Guid, CashVoucherType>()
            : await db.CashVoucherTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);

    public async Task<IReadOnlyList<CashVoucherType>> ListActiveAsync(CashDirection? direction, CancellationToken cancellationToken) =>
        await db.CashVoucherTypes.AsNoTracking()
            .Where(t => t.IsActive && (direction == null || t.Direction == direction))
            .OrderByDescending(t => t.IsSystem).ThenBy(t => t.Direction).ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

    public Task<CashVoucherType?> FindByNameAsync(CashDirection direction, string name, Guid? exceptTypeId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.CashVoucherTypes.AsNoTracking().FirstOrDefaultAsync(
            t => t.IsActive && t.Direction == direction && t.Name.ToUpper() == upper && t.Id != exceptTypeId, cancellationToken);
    }

    public Task<bool> IsUsedAsync(Guid typeId, CancellationToken cancellationToken) =>
        db.CashVouchers.AnyAsync(v => v.TypeId == typeId, cancellationToken);

    public void Add(CashVoucherType type) => db.CashVoucherTypes.Add(type);

    public void Remove(CashVoucherType type) => db.CashVoucherTypes.Remove(type);
}

internal sealed class CashVoucherRepository(OmtsDbContext db) : ICashVoucherRepository
{
    public Task<CashVoucher?> GetForEditAsync(Guid voucherId, CancellationToken cancellationToken) =>
        db.CashVouchers.Include(v => v.Type).Include(v => v.CashAccount).FirstOrDefaultAsync(v => v.Id == voucherId, cancellationToken);

    public async Task<Guid?> FindIdByCodeAsync(string code, Guid? exceptVoucherId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return await db.CashVouchers.AsNoTracking()
            .Where(v => v.Code.ToUpper() == upper && v.Id != exceptVoucherId)
            .Select(v => (Guid?)v.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public void Add(CashVoucher voucher) => db.CashVouchers.Add(voucher);
}

internal sealed class CashPartyRepository(OmtsDbContext db) : ICashPartyRepository
{
    public void Add(CashParty party) => db.CashParties.Add(party);
}
