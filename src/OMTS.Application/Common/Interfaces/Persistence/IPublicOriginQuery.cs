using OMTS.Application.Common.Models.Traceability;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>What the public provenance page a QR code opens reads (BR-ORIGIN-01, BR-ORIGIN-05). Anonymous and read only.</summary>
public interface IPublicOriginQuery
{
    /// <summary>The invoice that holds this QR token and is not cancelled; null when there is none.</summary>
    Task<PublicOriginSale?> SaleAsync(string qrToken, CancellationToken cancellationToken);

    /// <summary>The facts of these lots, in lot code order.</summary>
    Task<IReadOnlyList<PublicOriginLotFacts>> LotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);
}
