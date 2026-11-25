using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: MSG-SALE-01, 02, 03 against Part V (V:248-250), mandatory types, recipients and opening (BR-SALE-03, BR-ACCOUNT-11, BR-ACCOUNT-12, GFR-06).</summary>
public class PurchaseRequestQaNotificationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private sealed record World(Account OwnerAccount, HttpClient Owner, TraderActor Trader, Guid ProductId, string ProductName);

    private async Task<World> WorldAsync()
    {
        await factory.EnsureCooperativeAsync();
        var account = await factory.AddAccountAsync(NewCode(), Password);
        var owner = await factory.LoginAsync(account.Username);
        var product = await owner.CreateProductAsync("Hàu " + NewCode());
        return new World(account, owner, await factory.TraderAsync("Chị Hoa"), product.Id(), product.Text("name")!);
    }

    private static Dictionary<string, JsonElement> Parameters(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!)!;

    private static string Dmy(DateOnly date) => date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Msg_sale_01_reads_as_part_v_with_and_without_a_note_with_exactly_its_parameters()
    {
        var w = await WorldAsync();
        var pickup = PurchaseRequestTestKit.Today.AddDays(2);

        var withNote = await w.Trader.Client.PlaceAsync(w.ProductId, 1500, pickup, "Sáng sớm.");
        var without = await w.Trader.Client.PlaceAsync(w.ProductId, 1500, pickup);

        var row = Assert.Single((await factory.NotificationRowsAsync(withNote.Id())).Where(n => n.RecipientAccountId == w.OwnerAccount.Id));
        Assert.Equal(("MSG-SALE-01", NotificationTypes.PurchaseRequestNew, true), (row.MessageCode, row.Type, row.IsMandatory));
        Assert.Equal($"Chị Hoa asks to buy 1500 cages of {w.ProductName}, pickup on {Dmy(pickup)}. Sáng sớm. Please ask the Zone Manager to check the oysters, then respond.", row.Body);
        Assert.Equal(["customer_name", "note", "pickup_date", "product_name", "requested_cages"], Parameters(row.MessageParams).Keys.Order().ToList());
        Assert.Equal((1500, "Sáng sớm.", pickup.ToString("yyyy-MM-dd")), (Parameters(row.MessageParams)["requested_cages"].GetDecimal(), Parameters(row.MessageParams)["note"].GetString(), Parameters(row.MessageParams)["pickup_date"].GetString()));

        var plain = Assert.Single((await factory.NotificationRowsAsync(without.Id())).Where(n => n.RecipientAccountId == w.OwnerAccount.Id));
        Assert.Equal($"Chị Hoa asks to buy 1500 cages of {w.ProductName}, pickup on {Dmy(pickup)}. Please ask the Zone Manager to check the oysters, then respond.", plain.Body);
        Assert.Equal(JsonValueKind.Null, Parameters(plain.MessageParams)["note"].ValueKind);
        Assert.Null(plain.RelatedRecordCode);
    }

    [Fact]
    public async Task Msg_sale_02_reads_as_part_v_with_and_without_a_reason()
    {
        var w = await WorldAsync();
        var first = await w.Trader.Client.PlaceAsync(w.ProductId, 200);
        var second = await w.Trader.Client.PlaceAsync(w.ProductId, 200);

        (await w.Trader.Client.CancelAsync(first, "Khách đổi lịch")).EnsureSuccessStatusCode();
        (await w.Trader.Client.CancelAsync(second)).EnsureSuccessStatusCode();

        var today = Dmy(PurchaseRequestTestKit.Today);
        var row = Assert.Single((await factory.NotificationRowsAsync(first.Id())).OfType(NotificationTypes.PurchaseRequestCancelled).Where(n => n.RecipientAccountId == w.OwnerAccount.Id));
        Assert.Equal(("MSG-SALE-02", false), (row.MessageCode, row.IsMandatory));
        Assert.Equal($"Chị Hoa cancelled the purchase request of {today} for 200 cages of {w.ProductName}. Khách đổi lịch", row.Body);
        Assert.Equal(["customer_name", "product_name", "reason", "request_date", "requested_cages"], Parameters(row.MessageParams).Keys.Order().ToList());
        var plain = Assert.Single((await factory.NotificationRowsAsync(second.Id())).OfType(NotificationTypes.PurchaseRequestCancelled).Where(n => n.RecipientAccountId == w.OwnerAccount.Id));
        Assert.Equal($"Chị Hoa cancelled the purchase request of {today} for 200 cages of {w.ProductName}.", plain.Body);
    }

    [Fact]
    public async Task Msg_sale_03_reads_as_part_v_for_each_response()
    {
        var w = await WorldAsync();
        var coop = (await factory.EnsureCooperativeAsync()).FullName;
        var date = PurchaseRequestTestKit.Today.AddDays(3);

        var cases = new (Func<JsonElement, Task<HttpResponseMessage>> Answer, string Text)[]
        {
            (r => w.Owner.AcceptAsync(r, 250, date, true, "Đủ 250 kg"), $"{coop}: your request for 300 cages of {w.ProductName} is accepted for 250 cages, pickup on {Dmy(date)}; you may pay later. Đủ 250 kg"),
            (r => w.Owner.AcceptAsync(r, 300, date, false), $"{coop}: your request for 300 cages of {w.ProductName} is accepted for 300 cages, pickup on {Dmy(date)}."),
            (r => w.Owner.DeclineAsync(r, "Chưa đủ cỡ"), $"{coop}: your request for 300 cages of {w.ProductName} is declined. Chưa đủ cỡ"),
        };
        foreach (var (answer, text) in cases)
        {
            var request = await w.Trader.Client.PlaceAsync(w.ProductId, 300);
            (await answer(request)).EnsureSuccessStatusCode();

            var rows = (await factory.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestResponse).ToList();
            var listRow = Assert.Single(rows.InList());
            Assert.Equal(("MSG-SALE-03", w.Trader.Account.Id, false, text), (listRow.MessageCode, listRow.RecipientAccountId, listRow.IsMandatory, listRow.Body));
            Assert.Equal(text, Assert.Single(rows.Sendings()).Body);
            Assert.Equal(coop, Parameters(listRow.MessageParams)["cooperative_short_name"].GetString());
        }
    }

    // The attribute is in Part I (Cooperative, I:287), FARM-01 and five SMS templates. Without a saved profile the test above reads the full name.
    [Fact]
    public async Task L5_the_response_carries_the_short_name_of_the_cooperative_as_part_v_says()
    {
        const string shortName = "HTX Vân Đồn QA";
        var w = await WorldAsync();
        var cooperative = await factory.EnsureCooperativeAsync();
        var date = PurchaseRequestTestKit.Today.AddDays(3);
        await factory.UpdateAsync<Cooperative>(cooperative.Id, c => c.ShortName = shortName);
        try
        {
            var cases = new (Func<JsonElement, Task<HttpResponseMessage>> Answer, string Text)[]
            {
                (r => w.Owner.AcceptAsync(r, 250, date, false), $"{shortName}: your request for 300 cages of {w.ProductName} is accepted for 250 cages, pickup on {Dmy(date)}."),
                (r => w.Owner.DeclineAsync(r, "Chưa đủ cỡ"), $"{shortName}: your request for 300 cages of {w.ProductName} is declined. Chưa đủ cỡ"),
            };
            foreach (var (answer, text) in cases)
            {
                var request = await w.Trader.Client.PlaceAsync(w.ProductId, 300);
                (await answer(request)).EnsureSuccessStatusCode();

                var rows = (await factory.NotificationRowsAsync(request.Id())).OfType(NotificationTypes.PurchaseRequestResponse).ToList();
                var listRow = Assert.Single(rows.InList());
                Assert.Equal(text, listRow.Body);
                Assert.Equal(text, Assert.Single(rows.Sendings()).Body);
                Assert.Equal(shortName, Parameters(listRow.MessageParams)["cooperative_short_name"].GetString());
            }
        }
        finally
        {
            // The other tests of this class share the cooperative and read its full name.
            await factory.UpdateAsync<Cooperative>(cooperative.Id, c => c.ShortName = null);
        }
    }

    // BR-ACCOUNT-11: the new request is mandatory, the cancellation is not; both are kept in the Owner's list.
    [Fact]
    public async Task The_new_request_is_mandatory_and_the_cancellation_is_not_and_both_are_kept()
    {
        var w = await WorldAsync();

        var request = await w.Trader.Client.PlaceAsync(w.ProductId);
        (await w.Trader.Client.CancelAsync(request)).EnsureSuccessStatusCode();

        var rows = (await factory.NotificationRowsAsync(request.Id())).Where(n => n.RecipientAccountId == w.OwnerAccount.Id).ToList();
        Assert.True(rows.OfType(NotificationTypes.PurchaseRequestNew).Single().IsMandatory);
        Assert.False(rows.OfType(NotificationTypes.PurchaseRequestCancelled).Single().IsMandatory);
    }

    [Fact]
    public async Task Every_active_owner_is_told_and_a_locked_owner_and_the_staff_are_not()
    {
        var w = await WorldAsync();
        var second = await factory.AddAccountAsync(NewCode(), Password);
        var locked = await factory.AddAccountAsync(NewCode(), Password, configure: a => a.StatusId = StatusIds.Account.Locked);
        var staff = await factory.AddAccountAsync(NewCode(), Password, OMTS.Domain.Enums.SubjectType.Staff);
        await factory.GrantAsync(staff, OMTS.Domain.Enums.StaffRole.ZoneManager, null);
        var accountant = await factory.AddAccountAsync(NewCode(), Password, OMTS.Domain.Enums.SubjectType.Staff);
        await factory.GrantAsync(accountant, OMTS.Domain.Enums.StaffRole.Accountant, null);

        var request = await w.Trader.Client.PlaceAsync(w.ProductId);
        (await w.Trader.Client.CancelAsync(request)).EnsureSuccessStatusCode();

        var rows = await factory.NotificationRowsAsync(request.Id());
        foreach (var type in new[] { NotificationTypes.PurchaseRequestNew, NotificationTypes.PurchaseRequestCancelled })
        {
            var of = rows.OfType(type).Select(n => n.RecipientAccountId!.Value).ToList();
            Assert.Single(of, w.OwnerAccount.Id);
            Assert.Single(of, second.Id);
            Assert.DoesNotContain(locked.Id, of);
            Assert.DoesNotContain(staff.Id, of);
            Assert.DoesNotContain(accountant.Id, of);
            Assert.DoesNotContain(w.Trader.Account.Id, of);
        }
    }
}
