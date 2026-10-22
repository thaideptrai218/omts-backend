using OMTS.Application.Features.Employees;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>
/// FARM-03 create / edit / replace fields (multipart form next to the file). On replace an empty
/// <see cref="ZoneIds"/> keeps the zones of the replaced license; <see cref="RowVersion"/> is for edit only.
/// </summary>
public sealed record SaveZoneLicenseRequest
{
    public string? DocumentType { get; init; }
    public string? DocumentNumber { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? ExpiryDate { get; init; }
    public string? IssuingAuthority { get; init; }
    public string? Holder { get; init; }
    public string? LegalScope { get; init; }
    public List<Guid>? ZoneIds { get; init; }
    public string? RowVersion { get; init; }
}

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

/// <summary>One row of a zone's license overview; a Missing row has no document.</summary>
public sealed record ZoneLicenseRowView(
    string DocumentType,
    string DocumentTypeName,
    LicenseState State,
    Guid? DocumentId,
    string? DocumentNumber,
    DateOnly? EffectiveDate,
    DateOnly? ExpiryDate,
    bool IsReplaced);

public sealed record ZoneLicenseOverviewView(Guid ZoneId, DateOnly AsOf, IReadOnlyList<ZoneLicenseRowView> Licenses);
