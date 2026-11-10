using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223, SALE-04: linked profiles, repeated numbers and registration against staff saves.</summary>
public class SalesCatalogueQaLinkedProfileTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // SALE-04 POST-1, step 5: the address and the note are optional; a linked profile keeps them editable.
    [Fact]
    public async Task A_linked_profile_edits_its_name_address_and_note_with_any_spelling_of_its_number()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (trader, profile) = await factory.LinkedProfileAsync();
        var view = await owner.CustomerAsync(profile.Id);

        var saved = (await (await owner.PutAsJsonAsync($"/api/customers/{profile.Id}", new
        {
            name = "Edited", phoneNumber = $"+84 {trader.PhoneNumber![1..4]} {trader.PhoneNumber[4..7]} {trader.PhoneNumber[7..]}", address = "Hạ Long", note = "Sáng", rowVersion = view.RowVersion(),
        })).ReadAsync(HttpStatusCode.OK)).GetProperty("customer");

        Assert.Equal(("Edited", trader.PhoneNumber, "Hạ Long", "Sáng"), (saved.Text("name"), saved.Text("phoneNumber"), saved.Text("address"), saved.Text("note")));
        Assert.Equal(trader.Id, saved.GetProperty("traderAccount").GetProperty("accountId").GetGuid());
    }

    // A number entered on a linked profile with none: the number is changed only by the trader, and nothing is saved.
    [Fact]
    public async Task A_number_on_a_linked_profile_without_one_is_refused()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (_, profile) = await factory.LinkedProfileAsync(configure: c => c.PhoneNumber = null);
        var view = await owner.CustomerAsync(profile.Id);

        var response = await owner.PutAsJsonAsync($"/api/customers/{profile.Id}", new { name = "X", phoneNumber = NewPhone(), rowVersion = view.RowVersion() });

        await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal(JsonValueKind.Null, (await owner.CustomerAsync(profile.Id)).GetProperty("phoneNumber").ValueKind);
    }

    // BR-SALE-04: a profile made by registration may repeat a number; a staff save of ANOTHER number on the offline one clears it.
    [Fact]
    public async Task An_offline_profile_that_repeats_a_number_is_freed_by_a_new_number_or_by_the_merge()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (trader, linked) = await factory.LinkedProfileAsync();
        var cooperative = await factory.EnsureCooperativeAsync();
        var offline = new Customer { Code = OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit.NewCustomerCode(), CooperativeId = cooperative.Id, Name = "Offline", PhoneNumber = trader.PhoneNumber };
        await factory.UseDbAsync(db => db.Customers.Add(offline));
        var view = await owner.CustomerAsync(offline.Id);

        var moved = await owner.PutAsJsonAsync($"/api/customers/{offline.Id}", new { name = "Offline", phoneNumber = NewPhone(), rowVersion = view.RowVersion() });

        await moved.ReadAsync(HttpStatusCode.OK);
        Assert.True(await factory.QueryAsync(db => db.Customers.Where(c => c.Id == offline.Id).Select(c => c.PhoneCheckedByStaff).SingleAsync()));
        await (await owner.MergeAsync(linked.Id, offline.Id)).ReadAsync(HttpStatusCode.OK);
    }

    // BR-SALE-04: mandatory when staff edit; only a linked profile made with an email may stay without one.
    [Fact]
    public async Task An_offline_profile_with_no_number_needs_one_on_its_next_edit()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var cooperative = await factory.EnsureCooperativeAsync();
        var offline = new Customer { Code = OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit.NewCustomerCode(), CooperativeId = cooperative.Id, Name = "Old row" };
        await factory.UseDbAsync(db => db.Customers.Add(offline));
        var view = await owner.CustomerAsync(offline.Id);

        var without = await owner.PutAsJsonAsync($"/api/customers/{offline.Id}", new { name = "Old row", rowVersion = view.RowVersion() });
        Assert.Equal("Phone number", await without.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var phone = NewPhone();
        var with = (await (await owner.PutAsJsonAsync($"/api/customers/{offline.Id}", new { name = "Old row", phoneNumber = phone, rowVersion = view.RowVersion() })).ReadAsync(HttpStatusCode.OK)).GetProperty("customer");
        Assert.Equal(phone, with.Text("phoneNumber"));
    }

    // ACCOUNT-01: a number sent as +84 still links and still counts for the staff check.
    [Fact]
    public async Task Registration_with_a_plus_84_number_is_stored_as_0_and_counts_for_the_staff_check()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();
        var offline = await owner.CreateCustomerAsync(phone: phone);

        var accountId = await RegisterAsync($"+84{phone[1..]}", phone);

        var linked = await factory.QueryAsync(db => db.Customers.SingleAsync(c => c.AccountId == accountId));
        Assert.Equal((offline.Id(), phone), (linked.Id, linked.PhoneNumber));
    }

    // BR-ACCOUNT-04: only a profile not merged can be linked; the kept profile keeps the account it has.
    [Fact]
    public async Task Registration_with_the_number_of_a_merged_profile_makes_a_new_profile_or_links_the_new_owner_of_the_number()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (phone1, phone2) = (NewPhone(), NewPhone());
        var kept = await owner.CreateCustomerAsync();
        var merged1 = await owner.CreateCustomerAsync(phone: phone1);
        var merged2 = await owner.CreateCustomerAsync(phone: phone2);
        await (await owner.MergeAsync(kept.Id(), merged1.Id())).ReadAsync(HttpStatusCode.OK);
        await (await owner.MergeAsync(kept.Id(), merged2.Id())).ReadAsync(HttpStatusCode.OK);
        var reused = await owner.CreateCustomerAsync(phone: phone2);

        var alone = await RegisterAsync(phone1, phone1);
        var profile = await factory.QueryAsync(db => db.Customers.SingleAsync(c => c.AccountId == alone));
        Assert.NotEqual(merged1.Id(), profile.Id);
        Assert.Equal(phone1, profile.PhoneNumber);

        var second = await RegisterAsync(phone2, phone2);
        Assert.Equal(reused.Id(), (await factory.QueryAsync(db => db.Customers.SingleAsync(c => c.AccountId == second))).Id);
    }

    // NFR-09: a stale form gets MSG06. The number moved on: the screen read the old one, so it is a stale form, not a change.
    [Fact]
    public async Task L5_a_linked_form_read_before_the_trader_changed_the_number_gets_msg06()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (trader, profile) = await factory.LinkedProfileAsync();
        var view = await owner.CustomerAsync(profile.Id);
        await factory.UpdateAsync<Customer>(profile.Id, c => c.PhoneNumber = NewPhone());

        var response = await owner.PutAsJsonAsync($"/api/customers/{profile.Id}", new
        {
            name = "Edited", phoneNumber = trader.PhoneNumber, rowVersion = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
        });

        await response.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.NotEmpty(view.Text("name")!);
    }

    private async Task<Guid> RegisterAsync(string identifier, string normalized)
    {
        var client = factory.CreateClient();
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = identifier }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(normalized));
        var password = NewPassword();
        var response = await client.PostAsJsonAsync("/api/auth/register", new { channel = "Sms", identifier, password, passwordConfirmation = password, verificationToken = token });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accountId").GetGuid();
    }
}
