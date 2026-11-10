using OMTS.Application.Features.CashBook;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICashAccountRepository
{
    /// <summary>The account, tracked.</summary>
    Task<CashAccount?> GetForEditAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The accounts that exist among <paramref name="accountIds"/>, inactive ones included.</summary>
    Task<IReadOnlyDictionary<Guid, CashAccount>> GetByIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>Bank accounts first, then e-wallets, by provider and number.</summary>
    Task<IReadOnlyList<CashAccount>> ListAsync(CashAccountKind? kind, bool includeInactive, CancellationToken cancellationToken);

    /// <summary>Another account of the kind with the same provider and number, regardless of letter case.</summary>
    Task<CashAccount?> FindDuplicateAsync(CashAccountKind kind, string providerName, string accountNumber, Guid? exceptAccountId, CancellationToken cancellationToken);

    /// <summary>The Bank account of this bank and number, active or not, tracked.</summary>
    Task<CashAccount?> FindBankAccountAsync(string bankBin, string accountNumber, CancellationToken cancellationToken);

    /// <summary>True when a voucher or a supplier payment names the account.</summary>
    Task<bool> IsUsedAsync(Guid accountId, CancellationToken cancellationToken);

    void Add(CashAccount account);
    void Remove(CashAccount account);
}

public interface ICashVoucherTypeRepository
{
    /// <summary>The type, tracked.</summary>
    Task<CashVoucherType?> GetForEditAsync(Guid typeId, CancellationToken cancellationToken);

    /// <summary>The types that exist among <paramref name="typeIds"/>, hidden ones included.</summary>
    Task<IReadOnlyDictionary<Guid, CashVoucherType>> GetByIdsAsync(IReadOnlyCollection<Guid> typeIds, CancellationToken cancellationToken);

    /// <summary>The types not hidden, system ones first, then by name.</summary>
    Task<IReadOnlyList<CashVoucherType>> ListActiveAsync(CashDirection? direction, CancellationToken cancellationToken);

    /// <summary>Another listed (not hidden) type of the direction with the same name, regardless of letter case.</summary>
    Task<CashVoucherType?> FindByNameAsync(CashDirection direction, string name, Guid? exceptTypeId, CancellationToken cancellationToken);

    /// <summary>True when a voucher has the type.</summary>
    Task<bool> IsUsedAsync(Guid typeId, CancellationToken cancellationToken);

    void Add(CashVoucherType type);
    void Remove(CashVoucherType type);
}

public interface ICashVoucherRepository
{
    /// <summary>The voucher with its type and account, tracked.</summary>
    Task<CashVoucher?> GetForEditAsync(Guid voucherId, CancellationToken cancellationToken);

    /// <summary>The id of the voucher with this code regardless of letter case, other than <paramref name="exceptVoucherId"/>; cancelled ones included.</summary>
    Task<Guid?> FindIdByCodeAsync(string code, Guid? exceptVoucherId, CancellationToken cancellationToken);

    void Add(CashVoucher voucher);
}

public interface ICashPartyRepository
{
    void Add(CashParty party);
}

/// <summary>
/// One line of the cash book as the database reads it: a voucher, or a supplier payment with money (amount above zero). Text
/// values (<see cref="Direction"/>, <see cref="Fund"/>, <see cref="Source"/>, <see cref="PartyKind"/>) are the names of the enums,
/// since both kinds of row are read in one query.
/// </summary>
public sealed class CashBookEntry
{
    public string Source { get; init; } = string.Empty;
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }
    public DateOnly Date { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Direction { get; init; } = string.Empty;
    public string Fund { get; init; } = string.Empty;
    public Guid? CashAccountId { get; init; }
    public Guid TypeId { get; init; }
    public string? PartyKind { get; init; }
    public Guid? PartyId { get; init; }
    public string? PartyName { get; init; }
    public decimal Amount { get; init; }
    public Guid? CollectorId { get; init; }
    public Guid? CreatedById { get; init; }
    public string? Note { get; init; }
    public bool CountsInResult { get; init; }
    public bool Cancelled { get; init; }
    public string? SourceDocumentKind { get; init; }
    public Guid? SourceDocumentId { get; init; }
    public string? SourceDocumentCode { get; init; }
}

/// <summary>What the Recorded lines of a fund add up to: receipts and payments before a date, and within a period.</summary>
public sealed record CashBookSums(decimal ReceiptsBefore, decimal PaymentsBefore, decimal Receipts, decimal Payments);

public interface ICashBookQuery
{
    /// <summary>One page of the lines of <paramref name="query"/> in its order, and the number of lines of every page.</summary>
    Task<(IReadOnlyList<CashBookEntry> Items, int Total)> PageAsync(CashBookQuery query, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Every line of <paramref name="query"/>, in its order (the export).</summary>
    Task<IReadOnlyList<CashBookEntry>> ListAsync(CashBookQuery query, CancellationToken cancellationToken);

    /// <summary>The sums of the Recorded lines of the fund (and account) before <c>From</c> and within [<c>From</c>, <c>To</c>]; no other filter applies.</summary>
    Task<CashBookSums> SumsAsync(CashFundFilter fund, Guid? cashAccountId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);

    /// <summary>The parties of a kind matching the text in name, phone or code: customers not merged, active suppliers, any employee, parties made on a voucher.</summary>
    Task<IReadOnlyList<CashPartyView>> SearchPartiesAsync(CashPartyKind kind, string? search, int take, CancellationToken cancellationToken);

    /// <summary>The parties that exist among the keys (a customer, supplier, employee or voucher party), by key.</summary>
    Task<IReadOnlyDictionary<(CashPartyKind Kind, Guid Id), CashPartyView>> PartiesAsync(
        IReadOnlyCollection<(CashPartyKind Kind, Guid Id)> keys, CancellationToken cancellationToken);
}
