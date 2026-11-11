using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

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

    /// <summary>The receiving bank account (DEBT-01); null until one is accepted and saved.</summary>
    public ReceivingBankAccount? BankAccount { get; set; }

    /// <summary>The day of the month a pay period starts, 1 to 28 (LABOR-16, BR-LABOR-13).</summary>
    public int PayPeriodStartDay { get; set; } = 1;

    /// <summary>The daily check creates the payroll of each new pay period (LABOR-16, GFR-07).</summary>
    public bool AutomaticPayroll { get; set; }

    /// <summary>HARVEST-03: greater than 0, 50 when OMTS is first set up. Used only for lots recorded after a change (BR-HARVEST-06).</summary>
    public decimal AverageKgPerCage { get; set; } = 50m;

    /// <summary>HARVEST-03: a whole number of at least 1, 3 when OMTS is first set up. Applies to every lot still for sale from the next daily close check (BR-HARVEST-06).</summary>
    public int DaysBeforeAutoClose { get; set; } = 3;

    /// <summary>How the cost of input items is kept; Fixed when OMTS is first set up.</summary>
    public InputCostMethod InputCostMethod { get; set; } = InputCostMethod.Fixed;

    /// <summary>Whether a document may take an input item or seed lot below zero; true when OMTS is first set up.</summary>
    public bool AllowNegativeInputQuantity { get; set; } = true;

    /// <summary>Customers over the debt limit are flagged on the customer list and the invoice form; off when OMTS is first set up.</summary>
    public bool CustomerDebtWarningEnabled { get; set; }

    /// <summary>The most a customer may owe (VND, whole); null: the amount is not a limit. Only read while the warning is on.</summary>
    public decimal? CustomerMaxDebtAmount { get; set; }

    /// <summary>The most days a customer may owe, counted from the oldest unpaid debt invoice; null: the days are not a limit. Only read while the warning is on.</summary>
    public int? CustomerMaxDebtDays { get; set; }

    /// <summary>True: over the limit is only a warning. False: an invoice that leaves a debt for a customer over the limit is refused.</summary>
    public bool CustomerAllowMoreDebtOverLimit { get; set; } = true;

    /// <summary>A walk-in invoice must be paid in full; true when OMTS is first set up.</summary>
    public bool CustomerNoDebtForWalkIn { get; set; } = true;

    /// <summary>Optional fields of a customer profile the cooperative makes required (the name and the first phone number always are).</summary>
    public bool CustomerRequirePhoneNumber2 { get; set; }
    public bool CustomerRequireEmail { get; set; }
    public bool CustomerRequireGender { get; set; }
    public bool CustomerRequireBirthDate { get; set; }
    public bool CustomerRequireAddress { get; set; }
    public bool CustomerRequireFacebook { get; set; }
    public bool CustomerRequireBank { get; set; }
    public bool CustomerRequireTaxCode { get; set; }

    public ICollection<FarmingZone> FarmingZones { get; set; } = [];
    public ICollection<Employee> Employees { get; set; } = [];
    public ICollection<Customer> Customers { get; set; } = [];
    public ICollection<CooperativeCertification> Certifications { get; set; } = [];
}


