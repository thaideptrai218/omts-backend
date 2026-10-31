using OMTS.Application.Features.Employees;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>
/// FARM-03 create / edit / replace fields (multipart form next to the file). A license belongs to one zone: required on
/// create; on edit and replace it may be left out and cannot change. <see cref="RowVersion"/> is for edit only.
/// </summary>
public sealed record SaveZoneLicenseRequest
{
    /// <summary>Optional: SEA_AREA_ALLOCATION, AQUACULTURE_LICENSE, FOOD_SAFETY_COMMITMENT or OTHER; on replace, empty keeps the replaced one's.</summary>
    /// <example>AQUACULTURE_LICENSE</example>
    public string? DocumentType { get; init; }

    /// <summary>At most 100 characters.</summary>
    /// <example>125/GP-UBND</example>
    public string? DocumentNumber { get; init; }

    /// <summary>yyyy-MM-dd.</summary>
    /// <example>2026-01-15</example>
    public DateOnly? EffectiveDate { get; init; }

    /// <summary>yyyy-MM-dd, required and after the effective date.</summary>
    /// <example>2031-01-14</example>
    public DateOnly? ExpiryDate { get; init; }

    /// <summary>At most 200 characters.</summary>
    /// <example>UBND tỉnh Quảng Ninh</example>
    public string? IssuingAuthority { get; init; }

    /// <summary>Optional, at most 200 characters.</summary>
    /// <example>Hợp tác xã Nuôi trồng thủy sản Trung Nam</example>
    public string? Holder { get; init; }

    /// <summary>Optional, at most 1000 characters.</summary>
    /// <example>Nuôi hàu bè tại vùng biển xã Đông Xá, Vân Đồn</example>
    public string? LegalScope { get; init; }

    /// <summary>The zone of the license (In Use): required on create; on edit and replace empty keeps it, another zone is refused.</summary>
    /// <example>3fa85f64-5717-4562-b3fc-2c963f66afa6</example>
    public Guid? ZoneId { get; init; }

    /// <summary>Deprecated (one release): the older field, accepted with one zone only. Send <c>zoneId</c>.</summary>
    public List<Guid>? ZoneIds { get; init; }

    /// <summary>Edit only: the <c>rowVersion</c> the license was read with; an older one answers MSG06.</summary>
    /// <example>AAAAAAAAB9E=</example>
    public string? RowVersion { get; init; }
}

/// <param name="State">Valid, ExpiringSoon, Expired, NotYetEffective or Replaced on <see cref="AsOf"/> (BR-FARM-06).</param>
/// <param name="Zones">Deprecated (one release): <see cref="Zone"/> as a one-item list.</param>
/// <param name="FileUrl">Short-lived read URL of the license file.</param>
public sealed record ZoneLicenseView(
    Guid Id,
    string? DocumentType,
    string? DocumentTypeName,
    string DocumentNumber,
    DateOnly? EffectiveDate,
    DateOnly? ExpiryDate,
    string? IssuingAuthority,
    string? Holder,
    string? LegalScope,
    DateOnly AsOf,
    LicenseState State,
    ZoneRef Zone,
    IReadOnlyList<ZoneRef> Zones,
    Guid? ReplacesDocumentId,
    Guid? ReplacedByDocumentId,
    string? FileUrl,
    string RowVersion);

/// <summary>One license of a zone's list, with its state on the day asked for.</summary>
/// <param name="IsNewest">The newest license that is not Replaced: the one GFR-03 reminds.</param>
public sealed record ZoneLicenseRowView(
    string? DocumentType,
    string? DocumentTypeName,
    LicenseState State,
    Guid DocumentId,
    string DocumentNumber,
    DateOnly? EffectiveDate,
    DateOnly? ExpiryDate,
    bool IsReplaced,
    bool IsNewest);

/// <summary>The licenses of a zone, newest first.</summary>
public sealed record ZoneLicenseOverviewView(Guid ZoneId, DateOnly AsOf, IReadOnlyList<ZoneLicenseRowView> Licenses);
