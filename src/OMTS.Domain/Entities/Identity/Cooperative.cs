using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// The cooperative that runs OMTS (FARM-01). It has no status (SRS 27/10 §I): the record is created at deployment
/// and the Cooperative Owner keeps its profile up to date.
/// </summary>
public class Cooperative : AuditableEntity
{
    public string FullName { get; set; } = string.Empty;

    /// <summary>Used in SMS texts and on the provenance page; empty until the Owner first saves the profile.</summary>
    public string? ShortName { get; set; }

    /// <summary>Empty until the Owner first saves the profile (the deployment knows only the full name).</summary>
    public string? TaxCode { get; set; }

    public int? YearEstablished { get; set; }
    public string? OfficeAddress { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    /// <summary>Zalo OA or Facebook page link.</summary>
    public string? SocialLink { get; set; }

    /// <summary>Not in the SRS 27/10 profile; kept optional for older data.</summary>
    public string? LegalRepresentative { get; set; }

    /// <summary>JSON array of business line texts. Not in the SRS 27/10 profile; kept optional for older data.</summary>
    public string? BusinessLines { get; set; }

    /// <summary>
    /// Storage key of the logo shown on the public provenance page. Old logo files are never deleted, since
    /// published provenance versions may still reference them.
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>Storage key of the cover photo of the provenance page; a replaced or removed one is deleted.</summary>
    public string? CoverPhotoUrl { get; set; }

    /// <summary>Introduction in Vietnamese, shown on the provenance page (BR-ORIGIN-02).</summary>
    public string? IntroductionVi { get; set; }

    /// <summary>Introduction in English; the page shows the Vietnamese one when it is empty (BR-ORIGIN-02).</summary>
    public string? IntroductionEn { get; set; }

    /// <summary>Used to build the VietQR payment QR (UC48); never shown on the traceability page.</summary>
    public BankAccountInfo? BankAccount { get; set; }

    public ICollection<FarmingZone> FarmingZones { get; set; } = [];
    public ICollection<Employee> Employees { get; set; } = [];
    public ICollection<Customer> Customers { get; set; } = [];
    public ICollection<CooperativeCertification> Certifications { get; set; } = [];
}
