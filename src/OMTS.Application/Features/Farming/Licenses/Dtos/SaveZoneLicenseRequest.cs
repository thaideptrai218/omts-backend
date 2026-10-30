using OMTS.Application.Features.Employees;

namespace OMTS.Application.Features.Farming.Licenses.Dtos;

/// <summary>
/// FARM-03 create / edit / replace fields (multipart form next to the file). On replace an empty
/// <see cref="ZoneIds"/> keeps the zones of the replaced license; <see cref="RowVersion"/> is for edit only.
/// </summary>
public sealed record SaveZoneLicenseRequest
{
    /// <summary>SEA_AREA_ALLOCATION, AQUACULTURE_LICENSE, FOOD_SAFETY_COMMITMENT or OTHER; on replace, empty keeps the replaced one's.</summary>
    /// <example>AQUACULTURE_LICENSE</example>
    public string? DocumentType { get; init; }

    /// <summary>At most 100 characters.</summary>
    /// <example>125/GP-UBND</example>
    public string? DocumentNumber { get; init; }

    /// <summary>yyyy-MM-dd.</summary>
    /// <example>2026-01-15</example>
    public DateOnly? EffectiveDate { get; init; }

    /// <summary>yyyy-MM-dd, after the effective date; empty for a document without expiry.</summary>
    /// <example>2031-01-14</example>
    public DateOnly? ExpiryDate { get; init; }

    /// <summary>At most 200 characters.</summary>
    /// <example>UBND tỉnh Quảng Ninh</example>
    public string? IssuingAuthority { get; init; }

    /// <summary>At most 200 characters.</summary>
    /// <example>Hợp tác xã Nuôi trồng thủy sản Trung Nam</example>
    public string? Holder { get; init; }

    /// <summary>At most 1000 characters.</summary>
    /// <example>Nuôi hàu bè tại vùng biển xã Đông Xá, Vân Đồn</example>
    public string? LegalScope { get; init; }

    /// <summary>Covered zones: repeat this form field once per zone (the name ignores case, e.g. <c>zoneIds</c>).</summary>
    /// <example>["3fa85f64-5717-4562-b3fc-2c963f66afa6"]</example>
    public List<Guid>? ZoneIds { get; init; }

    /// <summary>Edit only: the <c>rowVersion</c> the license was read with; an older one answers MSG06.</summary>
    /// <example>AAAAAAAAB9E=</example>
    public string? RowVersion { get; init; }
}
