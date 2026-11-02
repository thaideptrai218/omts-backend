using OMTS.Application.Features.Employees.Dtos;
using OMTS.Application.Features.Employees;

namespace OMTS.Application.Features.Farming.Licenses.Dtos;

/// <param name="FileUrl">Short-lived read URL of the license file.</param>
public sealed record ZoneLicenseView(
    Guid Id,
    string DocumentType,
    string DocumentTypeName,
    string DocumentNumber,
    DateOnly? EffectiveDate,
    DateOnly? ExpiryDate,
    string? IssuingAuthority,
    string? Holder,
    string? LegalScope,
    DateOnly AsOf,
    LicenseState State,
    IReadOnlyList<ZoneRef> Zones,
    Guid? ReplacesDocumentId,
    Guid? ReplacedByDocumentId,
    string? FileUrl,
    string RowVersion);
