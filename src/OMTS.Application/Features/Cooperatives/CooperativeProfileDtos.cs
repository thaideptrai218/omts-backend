namespace OMTS.Application.Features.Cooperatives;

/// <summary>Exactly what the public provenance page shows about the cooperative (FARM-01).</summary>
public sealed record PublicCooperativePreview(string Name, string? OfficeAddress, string? LogoUrl);

/// <param name="Status">Preliminary until the first complete save, then Completed.</param>
public sealed record CooperativeProfileView(
    Guid Id,
    string FullName,
    string? TaxCode,
    string? OfficeAddress,
    string? PhoneNumber,
    string? Email,
    string? Website,
    string? LegalRepresentative,
    IReadOnlyList<string> BusinessLines,
    string? LogoUrl,
    string Status,
    PublicCooperativePreview PublicPreview,
    string RowVersion);

public sealed record UpdateCooperativeProfileRequest(
    string? FullName,
    string? TaxCode,
    string? OfficeAddress,
    string? PhoneNumber,
    string? Email,
    string? Website,
    string? LegalRepresentative,
    IReadOnlyList<string>? BusinessLines,
    string? RowVersion);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record CooperativeProfileSaved(CooperativeProfileView Profile, string Message, string MessageCode);
