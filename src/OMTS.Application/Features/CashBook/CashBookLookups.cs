using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>The records the lines of a page or the view of a voucher name, read once: their types, accounts, people and parties.</summary>
public sealed class CashBookLookups
{
    private readonly IReadOnlyDictionary<Guid, CashVoucherType> types;
    private readonly IReadOnlyDictionary<Guid, CashAccount> accounts;
    private readonly IReadOnlyDictionary<Guid, string> people;
    private readonly IReadOnlyDictionary<(CashPartyKind Kind, Guid Id), CashPartyView> parties;

    internal CashBookLookups(
        IReadOnlyDictionary<Guid, CashVoucherType> types,
        IReadOnlyDictionary<Guid, CashAccount> accounts,
        IReadOnlyDictionary<Guid, string> people,
        IReadOnlyDictionary<(CashPartyKind Kind, Guid Id), CashPartyView> parties)
    {
        this.types = types;
        this.accounts = accounts;
        this.people = people;
        this.parties = parties;
    }

    public CashTypeRef Type(Guid typeId) =>
        types.TryGetValue(typeId, out var type) ? new CashTypeRef(type.Id, type.Name, type.Category) : new CashTypeRef(typeId, string.Empty, CashCategory.OtherExpense);

    public CashAccountRef? Account(Guid? accountId) =>
        accountId is { } id && accounts.TryGetValue(id, out var account) ? new CashAccountRef(account.Id, account.ProviderName, account.AccountNumber) : null;

    /// <summary>An Owner or staff account by its display name.</summary>
    public AccountRef? Person(Guid? accountId) =>
        accountId is { } id ? new AccountRef(id, people.GetValueOrDefault(id) ?? string.Empty) : null;

    /// <summary>The party as the line names it: the name stored with the line, the code and phone number the record has now.</summary>
    public CashPartyRef? Party(CashPartyKind? kind, Guid? partyId, string? name)
    {
        if (kind is not { } k || (name is null && partyId is null))
        {
            return null;
        }

        var current = partyId is { } id && parties.TryGetValue((k, id), out var found) ? found : null;
        return new CashPartyRef(k, partyId, current?.Code, name ?? current?.Name ?? string.Empty, current?.PhoneNumber);
    }

    /// <summary>One line of the book as the API shows it.</summary>
    public CashBookRow Row(CashBookEntry e)
    {
        var direction = Enum.Parse<CashDirection>(e.Direction);
        var partyKind = e.PartyKind is null ? (CashPartyKind?)null : Enum.Parse<CashPartyKind>(e.PartyKind);
        return new CashBookRow(
            e.Id, Enum.Parse<CashSource>(e.Source), e.Code, e.OccurredAt, e.CreatedAt, direction, Enum.Parse<CashFund>(e.Fund), Account(e.CashAccountId),
            Type(e.TypeId), Party(partyKind, e.PartyId, e.PartyName), e.Amount, direction == CashDirection.Receipt ? e.Amount : -e.Amount,
            Person(e.CollectorId), Person(e.CreatedById), e.Note, e.CountsInResult, e.Cancelled ? CashEntryStatus.Cancelled : CashEntryStatus.Recorded,
            e.SourceDocumentId is { } sourceId ? new CashSourceDocument(e.SourceDocumentKind!, sourceId, e.SourceDocumentCode ?? string.Empty) : null);
    }
}

/// <summary>Reads the records a set of cash book lines name.</summary>
public sealed class CashBookLookupService(
    ICashVoucherTypeRepository types,
    ICashAccountRepository cashAccounts,
    IAccountRepository accounts,
    ICashBookQuery query)
{
    public async Task<CashBookLookups> LoadAsync(IReadOnlyList<CashBookEntry> entries, CancellationToken cancellationToken) => await LoadAsync(
        entries.Select(e => e.TypeId),
        entries.Where(e => e.CashAccountId is not null).Select(e => e.CashAccountId!.Value),
        entries.SelectMany(e => new[] { e.CollectorId, e.CreatedById }).Where(id => id is not null).Select(id => id!.Value),
        entries.Where(e => e.PartyKind is not null && e.PartyId is not null).Select(e => (Enum.Parse<CashPartyKind>(e.PartyKind!), e.PartyId!.Value)),
        cancellationToken);

    public async Task<CashBookLookups> LoadAsync(
        IEnumerable<Guid> typeIds, IEnumerable<Guid> accountIds, IEnumerable<Guid> personIds, IEnumerable<(CashPartyKind Kind, Guid Id)> partyKeys,
        CancellationToken cancellationToken) => new(
        await types.GetByIdsAsync(typeIds.Distinct().ToList(), cancellationToken),
        await cashAccounts.GetByIdsAsync(accountIds.Distinct().ToList(), cancellationToken),
        await accounts.GetDisplayNamesAsync(personIds.Distinct().ToList(), cancellationToken),
        await query.PartiesAsync(partyKeys.Distinct().ToList(), cancellationToken));
}
