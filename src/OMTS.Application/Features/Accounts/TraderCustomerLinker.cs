using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// Joins a trader account with the customer profiles the cooperative already keeps offline (BR-34):
/// exactly one offline profile with the verified number is linked or merged; several make the link
/// ambiguous, so old documents stay hidden (MSG57) until the Owner links them in customer management.
/// </summary>
public sealed class TraderCustomerLinker(ICustomerRepository customers)
{
    /// <summary>
    /// Registration (ACS-01): link the uniquely matching offline profile, otherwise create a new one. Email
    /// registrations always get a new profile (the phone is added after the first login).
    /// </summary>
    public async Task<(Customer Customer, bool OldDocumentsHidden)> LinkNewTraderAsync(
        Account trader, Guid cooperativeId, CancellationToken cancellationToken)
    {
        var offline = trader.PhoneNumber is { } phone ? await customers.GetOfflineByPhoneAsync(phone, cancellationToken) : [];
        if (offline.Count == 1)
        {
            offline[0].AccountId = trader.Id;
            return (offline[0], false);
        }

        var customer = new Customer
        {
            CooperativeId = cooperativeId,
            AccountId = trader.Id,
            Name = trader.DisplayName,
            PhoneNumber = trader.PhoneNumber,
            Email = trader.Email,
        };
        customers.Add(customer);
        return (customer, offline.Count > 1);
    }

    /// <summary>
    /// A trader verified a (new) phone number (ACS-02 phone step, ACS-05 phone change): merge the uniquely
    /// matching offline profile into the trader's own profile; otherwise only update its contact number.
    /// Returns whether old documents stay hidden.
    /// </summary>
    public async Task<bool> ApplyVerifiedPhoneAsync(Account trader, string phoneNumber, CancellationToken cancellationToken)
    {
        var own = await customers.GetByAccountIdAsync(trader.Id, cancellationToken);
        var offline = (await customers.GetOfflineByPhoneAsync(phoneNumber, cancellationToken)).Where(c => c.Id != own?.Id).ToList();
        if (own is null)
        {
            return false;
        }

        own.PhoneNumber = phoneNumber;
        if (offline.Count == 1)
        {
            offline[0].MergedIntoCustomerId = own.Id;
        }

        return offline.Count > 1;
    }
}
