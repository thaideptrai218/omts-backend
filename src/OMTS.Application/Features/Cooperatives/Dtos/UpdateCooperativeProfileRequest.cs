
namespace OMTS.Application.Features.Cooperatives.Dtos;

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
