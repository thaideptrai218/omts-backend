using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Domain.Entities.Identity;

/// <summary>E01 – The party that owns zones, staff, orders and traceability data.</summary>
public class Cooperative : AuditableEntity, IHasStatus
{
    public string FullName { get; set; } = string.Empty;

    /// <summary>Empty while the cooperative is Preliminary (set up by UC72).</summary>
    public string? TaxCode { get; set; }

    public string? OfficeAddress { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? LegalRepresentative { get; set; }
    /// <summary>JSON array of business line texts (at least one once the profile is Completed).</summary>
    public string? BusinessLines { get; set; }

    /// <summary>
    /// Storage key of the logo shown on the public provenance page. Old logo files are never deleted, since
    /// published provenance versions may still reference them.
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>Used to build the VietQR payment QR (UC48); never shown on the traceability page.</summary>
    public BankAccountInfo? BankAccount { get; set; }

    public int StatusId { get; set; } = StatusIds.Cooperative.Draft;
    public Status Status { get; set; } = null!;

    public ICollection<FarmingZone> FarmingZones { get; set; } = [];
    public ICollection<Employee> Employees { get; set; } = [];
    public ICollection<Customer> Customers { get; set; } = [];
}
