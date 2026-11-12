using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// A trader account is linked to one customer profile (BR-ACCOUNT-04): at registration, the one profile with the
/// verified number and no account, otherwise a new profile. The link never changes afterwards; a Zone Manager merges
/// profiles of the same trader in Manage customers (SALE-04).
/// </summary>
public sealed class TraderCustomerLinker(ICustomerRepository customers, CustomerCodeService codes)
{
    /// <summary>
    /// Registration (ACCOUNT-01 steps 6 and ACCOUNT-01.1): links the uniquely matching profile, otherwise creates a new
    /// one. Email registrations always get a new profile (the phone is added after the first login).
    /// </summary>
    /// <returns>The profile, and whether it is an existing one (MSG-ACCOUNT-01) or a new one (MSG-ACCOUNT-02).</returns>
    public async Task<(Customer Customer, bool LinkedExisting)> LinkNewTraderAsync(
        Account trader, Guid cooperativeId, CancellationToken cancellationToken)
    {
        var offline = trader.PhoneNumber is { } phone ? await customers.GetOfflineByPhoneAsync(phone, cancellationToken) : [];
        if (offline.Count == 1)
        {
            offline[0].AccountId = trader.Id;
            return (offline[0], true);
        }

        var customer = new Customer
        {
            Code = await codes.NextAsync(cancellationToken),
            CooperativeId = cooperativeId,
            AccountId = trader.Id,
            Name = trader.DisplayName,
            PhoneNumber = trader.PhoneNumber,
            Email = trader.Email,
        };
        customers.Add(customer);
        return (customer, false);
    }

    /// <summary>
    /// A trader account got or changed its phone number (ACCOUNT-02.1, ACCOUNT-05.1): the link stays and its profile gets
    /// the number; another profile that has this number is not linked (BR-ACCOUNT-04).
    /// </summary>
    public async Task ApplyVerifiedPhoneAsync(Account trader, string phoneNumber, CancellationToken cancellationToken)
    {
        if (await customers.GetByAccountIdAsync(trader.Id, cancellationToken) is { } own)
        {
            own.PhoneNumber = phoneNumber;
        }
    }
}
