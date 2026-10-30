
namespace OMTS.Application.Features.Cooperatives.Dtos;

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
