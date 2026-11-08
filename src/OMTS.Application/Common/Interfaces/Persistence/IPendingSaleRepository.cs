using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The sales awaiting the transfer of their VietQR code (SALE-05 steps 7-8).</summary>
public interface IPendingSaleRepository
{
    void Add(PendingSale sale);

    /// <summary>Tracked, with its zone and customer: what a change of payment, a stop or the bank notification changes.</summary>
    Task<PendingSale?> GetForEditAsync(string code, CancellationToken cancellationToken);

    /// <summary>Read only, with its zone and customer.</summary>
    Task<PendingSale?> GetAsync(string code, CancellationToken cancellationToken);

    /// <summary>The code of the sale created with this key, if any.</summary>
    Task<string?> FindCodeByDeduplicationKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>The sales still Awaiting in these zones (every zone when null), oldest first, read only with their zone and customer.</summary>
    Task<IReadOnlyList<PendingSale>> ListAwaitingAsync(IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken);
}
