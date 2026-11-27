using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A product as the public pages show it: never its price, stock or internal id.</summary>
public sealed record ShowcaseProductRow(string Name, string? Description, decimal? SizeMinCm, decimal? SizeMaxCm, IReadOnlyList<string> PhotoKeys);

public interface ILandingRepository
{
    /// <summary>Every picture, by section then order; tracked, so one can be removed.</summary>
    Task<List<LandingPhoto>> PhotosAsync(CancellationToken cancellationToken);

    void Add(LandingPhoto photo);

    void Remove(LandingPhoto photo);

    /// <summary>The products on sale on <paramref name="today"/> (in use, not deleted, inside their validity window), by name.</summary>
    Task<IReadOnlyList<ShowcaseProductRow>> ProductsOnSaleAsync(DateOnly today, CancellationToken cancellationToken);

    /// <summary>The products of these names, which are unique.</summary>
    Task<IReadOnlyList<ShowcaseProductRow>> ProductsByNameAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken);

    /// <summary>The QR code of the invoice with this code, or null when there is none to show (unknown, cancelled).</summary>
    Task<string?> QrTokenOfAsync(string invoiceCode, CancellationToken cancellationToken);
}
