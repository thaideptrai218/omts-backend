using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// The customer profile of a trader (SALE-04, BR-SALE-04). It may exist before the trader has an account and is linked to
/// at most one account (BR-ACCOUNT-04). It has no zone. A profile that has been used is Discontinued instead of deleted and can be
/// resumed; "Merged" is worked out from <see cref="MergedIntoCustomerId"/>. A walk-in customer has no profile at all (BR-SALE-07).
/// </summary>
public class Customer : AuditableEntity, IHasStatus
{
    private string? _phoneNumber;

    /// <summary>KH000123: one cooperative-wide sequence; the user may type another code, unique regardless of letter case.</summary>
    public string Code { get; set; } = string.Empty;

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    public CustomerType CustomerType { get; set; }
    public Gender? Gender { get; set; }
    public DateOnly? BirthDate { get; set; }

    /// <summary>A second contact number; not unique and not checked against the other profiles.</summary>
    public string? PhoneNumber2 { get; set; }

    public string? Facebook { get; set; }

    /// <summary>Storage key of the photo (JPEG or PNG, at most 2 MB).</summary>
    public string? PhotoUrl { get; set; }

    public string? Province { get; set; }
    public string? Ward { get; set; }

    /// <summary>Invoice data: the company and its tax code.</summary>
    public string? CompanyName { get; set; }
    public string? TaxCode { get; set; }

    /// <summary>Personal data (CCCD): shown on the detail only, never in a list, an export or the audit log.</summary>
    public string? IdentityNumber { get; set; }

    /// <summary>The bank account the customer is paid back to: the bank of the bank list and the number.</summary>
    public string? BankBin { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }

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
