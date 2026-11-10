using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.CashBook;

/// <summary>A payer or receiver who is neither a customer, a supplier nor an employee ("Khác"), made on the spot from a voucher.</summary>
public class CashParty : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public string? Area { get; set; }
    public string? Ward { get; set; }
    public string? Note { get; set; }
}
