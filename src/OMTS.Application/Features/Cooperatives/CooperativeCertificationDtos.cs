namespace OMTS.Application.Features.Cooperatives;

/// <summary>FARM-01.1 add or edit fields (multipart form next to the file in <c>file</c>).</summary>
public sealed record SaveCooperativeCertificationRequest
{
    /// <summary>Required, at most 200 characters.</summary>
    /// <example>Chứng nhận VietGAP</example>
    public string? Name { get; init; }

    /// <summary>At most 100 characters.</summary>
    /// <example>VG-QN-2026-015</example>
    public string? Number { get; init; }

    /// <summary>At most 200 characters.</summary>
    /// <example>Trung tâm Chứng nhận Phù hợp Quacert</example>
    public string? Issuer { get; init; }

    /// <summary>yyyy-MM-dd; empty for a certification that does not expire.</summary>
    /// <example>2028-06-30</example>
    public DateOnly? ExpiryDate { get; init; }

    /// <summary>Edit only: the <c>rowVersion</c> the certification was read with; an older one answers MSG06.</summary>
    /// <example>AAAAAAAAB9E=</example>
    public string? RowVersion { get; init; }
}

/// <param name="State">Valid, ExpiringSoon or Expired as of today (BR-FARM-06).</param>
/// <param name="FileUrl">Short-lived read URL of the certificate file.</param>
public sealed record CooperativeCertificationView(
    Guid Id,
    string Name,
    string? Number,
    string? Issuer,
    DateOnly? ExpiryDate,
    CertificationState State,
    string? FileUrl,
    string RowVersion);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record CooperativeCertificationSaved(CooperativeCertificationView Certification, string Message, string MessageCode);
