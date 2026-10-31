namespace OMTS.Application.Features.Cooperatives;

/// <summary>A certification as the provenance page shows it: only those that are not Expired (BR-FARM-09).</summary>
/// <param name="State">Valid or ExpiringSoon.</param>
public sealed record PublicCertification(string Name, string? Number, string? Issuer, DateOnly? ExpiryDate, CertificationState State);

/// <summary>
/// The cooperative block of the provenance page, exactly as the public page will show it (BR-FARM-09, BR-ORIGIN-02):
/// never the tax code, the legal representative, the business lines or an Expired certification.
/// </summary>
/// <param name="IntroductionEn">The English introduction, or the Vietnamese one when it is empty (BR-ORIGIN-02).</param>
public sealed record PublicCooperativePreview(
    string FullName,
    string? ShortName,
    int? YearEstablished,
    string? OfficeAddress,
    string? PhoneNumber,
    string? Email,
    string? Website,
    string? SocialLink,
    string? LogoUrl,
    string? CoverPhotoUrl,
    string? IntroductionVi,
    string? IntroductionEn,
    IReadOnlyList<PublicCertification> Certifications);

/// <param name="LogoUrl">Public URL of this logo file (anonymous).</param>
/// <param name="CoverPhotoUrl">Public URL of the current cover photo (anonymous).</param>
/// <param name="AsOf">The business date the certification states are worked out for (today).</param>
/// <param name="Certifications">Every certification with its state, by name.</param>
/// <param name="PublicPreview">What the provenance page will show (FARM-01 step 3).</param>
public sealed record CooperativeProfileView(
    Guid Id,
    string FullName,
    string? ShortName,
    string? TaxCode,
    int? YearEstablished,
    string? OfficeAddress,
    string? PhoneNumber,
    string? Email,
    string? Website,
    string? SocialLink,
    string? IntroductionVi,
    string? IntroductionEn,
    string? LegalRepresentative,
    IReadOnlyList<string> BusinessLines,
    string? LogoUrl,
    string? CoverPhotoUrl,
    DateOnly AsOf,
    IReadOnlyList<CooperativeCertificationView> Certifications,
    PublicCooperativePreview PublicPreview,
    string RowVersion);

/// <summary>
/// FARM-01 profile fields. Mandatory: full name, short name, tax code, office address and phone number; the others
/// may be empty. Logo and cover photo have endpoints of their own.
/// </summary>
public sealed record UpdateCooperativeProfileRequest
{
    /// <summary>At most 255 characters.</summary>
    /// <example>Hợp tác xã Nuôi trồng thủy sản Trung Nam</example>
    public string? FullName { get; init; }

    /// <summary>At most 100 characters; used in SMS texts and on the provenance page.</summary>
    /// <example>HTX Trung Nam</example>
    public string? ShortName { get; init; }

    /// <summary>10 digits, or 10 digits, a hyphen and 3 digits (branch).</summary>
    /// <example>0109876543</example>
    public string? TaxCode { get; init; }

    /// <summary>From 1900 to the current year.</summary>
    /// <example>2015</example>
    public int? YearEstablished { get; init; }

    /// <summary>At most 500 characters.</summary>
    /// <example>Thôn 3, xã Đông Xá, Vân Đồn, Quảng Ninh</example>
    public string? OfficeAddress { get; init; }

    /// <example>0912345678</example>
    public string? PhoneNumber { get; init; }

    /// <example>lienhe@htxtrungnam.vn</example>
    public string? Email { get; init; }

    /// <summary>http or https URL, at most 255 characters.</summary>
    /// <example>https://htxtrungnam.vn</example>
    public string? Website { get; init; }

    /// <summary>Zalo OA or Facebook link: https URL, at most 255 characters.</summary>
    /// <example>https://www.facebook.com/htxtrungnam</example>
    public string? SocialLink { get; init; }

    /// <summary>At most 4000 characters.</summary>
    public string? IntroductionVi { get; init; }

    /// <summary>At most 4000 characters; empty shows the Vietnamese one on the English page.</summary>
    public string? IntroductionEn { get; init; }

    /// <summary>Optional (not in the SRS 27/10 profile), at most 255 characters.</summary>
    public string? LegalRepresentative { get; init; }

    /// <summary>Optional (not in the SRS 27/10 profile): each line 1–255 characters.</summary>
    public IReadOnlyList<string>? BusinessLines { get; init; }

    /// <summary>The <c>rowVersion</c> the profile was read with; an older one answers MSG06.</summary>
    public string? RowVersion { get; init; }
}

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record CooperativeProfileSaved(CooperativeProfileView Profile, string Message, string MessageCode);
