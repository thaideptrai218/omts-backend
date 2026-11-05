using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// The customer profile of a trader (SALE-04, BR-SALE-04). It may exist before the trader has an account and is linked to
/// at most one account (BR-ACCOUNT-04). It has no zone and no status: "Merged" is worked out from
/// <see cref="MergedIntoCustomerId"/>. A walk-in customer has no profile at all (BR-SALE-07).
/// </summary>
public class Customer : AuditableEntity
{
    private string? _phoneNumber;

    public Guid CooperativeId { get; set; }
    public Cooperative Cooperative { get; set; } = null!;

    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Mandatory when staff save the profile; a profile made at registration with an email may have none. Any write other
    /// than a staff save (registration, a trader changing the number) leaves it unchecked, see <see cref="PhoneCheckedByStaff"/>.
    /// </summary>
    public string? PhoneNumber
    {
        get => _phoneNumber;
        set
        {
            if (value != _phoneNumber)
            {
                PhoneCheckedByStaff = false;
            }

            _phoneNumber = value;
        }
    }

    /// <summary>
    /// True while the number is the one staff saved after checking it against the other profiles. A filtered unique index
    /// covers these rows only, so two parallel staff saves cannot both win, while the profiles that registration creates or
    /// updates stay unchecked and may repeat a number until they are merged (BR-SALE-04). Set it after <see cref="PhoneNumber"/>.
    /// </summary>
    public bool PhoneCheckedByStaff { get; set; }

    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Note { get; set; }

    /// <summary>Not in the SRS and not used by any service; kept until the Zalo work of PR #20 is settled.</summary>
    public string? ZaloAccountId { get; set; }

    /// <summary>Set when this profile was merged into another one (SALE-04.3): it is no longer listed or chosen.</summary>
    public Guid? MergedIntoCustomerId { get; set; }
    public Customer? MergedIntoCustomer { get; set; }
}
