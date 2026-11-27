using OMTS.Application.Features.Traceability.Origin;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Cooperatives.Landing;

/// <param name="Url">A signed read URL: ask again rather than keeping it.</param>
public sealed record LandingPhotoView(Guid Id, LandingSection Section, string? Caption, string Url);

/// <summary>What the Owner manages: the pictures and the invoice shown as the example of the provenance page.</summary>
public sealed record LandingSettingsView(IReadOnlyList<LandingPhotoView> Photos, string? FeaturedInvoiceCode);

public sealed record SetFeaturedInvoiceRequest(string? InvoiceCode);

public sealed record LandingPublicPhoto(LandingSection Section, string? Caption, string Url);

/// <summary>
/// The public introduction page. Like the provenance page it is a whitelist: no price, stock, customer or internal id.
/// </summary>
/// <param name="FeaturedQrToken">The QR code of the example invoice the Owner chose; null when none is chosen or it was cancelled.</param>
public sealed record LandingPublicView(
    ProvenanceCooperative Cooperative,
    IReadOnlyList<LandingPublicPhoto> Photos,
    IReadOnlyList<ProvenanceProduct> Products,
    string? FeaturedQrToken);
