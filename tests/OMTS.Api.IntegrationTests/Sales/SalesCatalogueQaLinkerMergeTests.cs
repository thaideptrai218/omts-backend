using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223, BR-ACCOUNT-04 and SALE-04.3: the merge the linker does on its own when a trader verifies a number.</summary>
public class SalesCatalogueQaLinkerMergeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task AssertNoChainAsync()
    {
        var rows = await factory.QueryAsync(db => db.Customers.AsNoTracking().Select(c => new { c.Id, c.MergedIntoCustomerId }).ToListAsync());
        var merged = rows.Where(r => r.MergedIntoCustomerId is not null).Select(r => r.Id).ToHashSet();
        Assert.DoesNotContain(rows, r => r.MergedIntoCustomerId is { } target && merged.Contains(target));
    }

    /// <summary>A trader whose account has its own number, with its own profile, logged in.</summary>
    private async Task<(Account Trader, Customer Own, HttpClient Client, string Password)> TraderAsync(Action<Account>? configure = null)
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var trader = await factory.AddAccountAsync(NewCode(), password, SubjectType.Trader, configure);
        var own = new Customer { CooperativeId = cooperative.Id, AccountId = trader.Id, Name = $"Own {NewCode()}", PhoneNumber = trader.PhoneNumber };
        await factory.UseDbAsync(db => db.Customers.Add(own));
        return (trader, own, await factory.LoginAsync(trader.Username, password), password);
    }

    private async Task ChangeNumberAsync(HttpClient client, string password, string phone)
    {
        var started = await client.PostAsJsonAsync("/api/me/contact-changes", new { channel = "Sms", target = phone, currentPassword = password });
        var token = await VerifyAsync(client, await VerificationIdAsync(started), factory.LastSmsCode(phone));
        await (await client.PostAsJsonAsync("/api/me/contact-changes/confirm", new { verificationToken = token })).ReadAsync(HttpStatusCode.OK);
    }

    // BR-ACCOUNT-04, ACCOUNT-05: "the link stays and the system updates the phone number of the customer profile; the system
    // does not link the account to another customer profile that has the new phone number". BR-SALE-04: a Zone Manager merges,
    // after the Owner agreed, and a merge cannot be undone.
    [Fact]
    public async Task H1_a_traders_new_number_does_not_merge_the_offline_profile_that_has_it()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (_, own, client, password) = await TraderAsync();
        var phone = NewPhone();
        var offline = await owner.CreateCustomerAsync(name: $"Offline {NewCode()}", phone: phone);

        await ChangeNumberAsync(client, password, phone);

        Assert.Equal(phone, (await owner.CustomerAsync(own.Id)).Text("phoneNumber"));
        Assert.Equal(JsonValueKind.Null, (await owner.CustomerAsync(offline.Id())).GetProperty("mergedIntoCustomerId").ValueKind);
    }

    // BR-ACCOUNT-04, ACCOUNT-02: the same for the first number of a trader who registered with an email.
    [Fact]
    public async Task H1_a_traders_first_verified_number_does_not_merge_the_offline_profile_that_has_it()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (_, _, client, _) = await TraderAsync(a => { a.PhoneNumber = null; a.PhoneVerifiedAt = null; });
        var phone = NewPhone();
        var offline = await owner.CreateCustomerAsync(name: $"Offline {NewCode()}", phone: phone);

        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = phone }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(phone));
        await (await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, (await owner.CustomerAsync(offline.Id())).GetProperty("mergedIntoCustomerId").ValueKind);
    }

    // The promise of this PR (client list §3): a merged profile always points at a kept one.
    [Fact]
    public async Task H1_a_number_change_leaves_no_chain_of_merged_profiles()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (_, _, client, password) = await TraderAsync();
        var phone = NewPhone();
        var offline = await owner.CreateCustomerAsync(phone: phone);
        var earlier = await owner.CreateCustomerAsync();
        await (await owner.MergeAsync(offline.Id(), earlier.Id())).ReadAsync(HttpStatusCode.OK);

        await ChangeNumberAsync(client, password, phone);

        await AssertNoChainAsync();
    }

    // GBR-02: a merge is written to the audit log, whoever does it; no merge, no row.
    [Fact]
    public async Task H1_a_profile_merged_on_a_number_change_has_its_audit_row()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (_, _, client, password) = await TraderAsync();
        var phone = NewPhone();
        var offline = await owner.CreateCustomerAsync(phone: phone);

        await ChangeNumberAsync(client, password, phone);

        var isMerged = (await owner.CustomerAsync(offline.Id())).GetProperty("mergedIntoCustomerId").ValueKind != JsonValueKind.Null;
        Assert.Equal(isMerged, (await factory.AuditActionsAsync(offline.Id())).Contains("Customer.Merged"));
    }
}
