using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-04.2, BR-SALE-04, BR-ACCOUNT-04: the phone number of a linked profile follows the account.</summary>
public class CustomerLinkedPhoneTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // SALE-04.2, BR-SALE-04: the number of a linked profile follows the account and is changed only by the trader.
    [Fact]
    public async Task The_phone_number_of_a_linked_profile_is_locked()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (trader, profile) = await factory.LinkedProfileAsync(name: "Trader");
        var view = await owner.CustomerAsync(profile.Id);
        Assert.True(view.GetProperty("phoneLocked").GetBoolean());

        var changed = await owner.PutAsJsonAsync($"/api/customers/{profile.Id}", new { name = "Trader", phoneNumber = NewPhone(), rowVersion = view.RowVersion() });
        var refused = await changed.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Phone number", refused.Text("field"));
        Assert.True(refused.TryGetProperty("hint", out _));
        Assert.Equal(trader.PhoneNumber, (await owner.CustomerAsync(profile.Id)).Text("phoneNumber"));

        var same = await (await owner.PutAsJsonAsync($"/api/customers/{profile.Id}", new { name = "Trader 2", phoneNumber = trader.PhoneNumber, rowVersion = view.RowVersion() }))
            .ReadAsync(HttpStatusCode.OK);
        var left = await (await owner.PutAsJsonAsync($"/api/customers/{profile.Id}", new { name = "Trader 3", address = "Hạ Long", rowVersion = same.GetProperty("customer").RowVersion() }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Trader 3", trader.PhoneNumber), (left.GetProperty("customer").Text("name"), left.GetProperty("customer").Text("phoneNumber")));
    }

    // BR-SALE-04: a profile made at registration with an email may have no number until the trader adds one.
    [Fact]
    public async Task A_linked_profile_with_no_number_can_be_edited_without_one()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (_, profile) = await factory.LinkedProfileAsync(configure: c => c.PhoneNumber = null);
        var view = await owner.CustomerAsync(profile.Id);

        var response = await owner.PutAsJsonAsync($"/api/customers/{profile.Id}", new { name = "By email", rowVersion = view.RowVersion() });

        var saved = (await response.ReadAsync(HttpStatusCode.OK)).GetProperty("customer");
        Assert.Equal(("By email", JsonValueKind.Null), (saved.Text("name"), saved.GetProperty("phoneNumber").ValueKind));
    }

    // A linked profile that repeats a number (the trader chose it later) can still be edited: its number is not entered here.
    [Fact]
    public async Task A_linked_profile_that_repeats_a_number_can_still_be_edited_but_the_offline_one_must_be_merged()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (trader, linked) = await factory.LinkedProfileAsync();
        var cooperative = await factory.EnsureCooperativeAsync();
        var offline = new Customer { Code = OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit.NewCustomerCode(), CooperativeId = cooperative.Id, Name = "Offline", PhoneNumber = trader.PhoneNumber };
        await factory.UseDbAsync(db => db.Customers.Add(offline));

        var linkedView = await owner.CustomerAsync(linked.Id);
        await (await owner.PutAsJsonAsync($"/api/customers/{linked.Id}", new { name = "Edited", rowVersion = linkedView.RowVersion() })).ReadAsync(HttpStatusCode.OK);

        var offlineView = await owner.CustomerAsync(offline.Id);
        var refused = await owner.PutAsJsonAsync($"/api/customers/{offline.Id}", new { name = "Edited", phoneNumber = trader.PhoneNumber, rowVersion = offlineView.RowVersion() });
        Assert.Equal(linked.Id, (await refused.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
    }

    [Fact]
    public async Task The_view_shows_the_linked_trader_account()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (trader, profile) = await factory.LinkedProfileAsync();

        var account = (await owner.CustomerAsync(profile.Id)).GetProperty("traderAccount");

        Assert.Equal(trader.Id, account.GetProperty("accountId").GetGuid());
        Assert.Equal(trader.Username, account.Text("username"));
    }

    // The flag on the entity: a write other than a staff save leaves the number unchecked (BR-ACCOUNT-04).
    [Fact]
    public void A_number_written_outside_a_staff_save_is_no_longer_checked_by_staff()
    {
        var customer = new Customer { PhoneNumber = "0900000001", PhoneCheckedByStaff = true };

        customer.PhoneNumber = "0900000001";
        Assert.True(customer.PhoneCheckedByStaff);

        customer.PhoneNumber = "0900000002";
        Assert.False(customer.PhoneCheckedByStaff);
    }
}
