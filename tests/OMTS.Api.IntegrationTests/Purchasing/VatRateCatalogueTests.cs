using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// The VAT rate list ("Thuế GTGT"): the seeded rates, who reads and who writes, the unique name, KCT locked, one default, deactivating with a replacement that takes the items along, and the audit rows.
/// </summary>
public class VatRateCatalogueTests
{
    private const string Rates = "/api/vat-rates";

    private static async Task<JsonElement> RateAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>(Rates)).EnumerateArray().Single(r => r.Id() == id);

    private static string Version(JsonElement rate) => rate.GetProperty("rowVersion").GetString()!;

    [Fact]
    public async Task The_list_starts_with_KCT_0_5_8_and_10_percent_and_the_ten_is_the_default()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var rates = (await owner.GetFromJsonAsync<JsonElement>(Rates)).EnumerateArray().ToList();

        Assert.Equal(["KCT", "0%", "5%", "8%", "10%"], rates.Select(r => r.GetProperty("name").GetString()));
        var kct = rates[0];
        Assert.Equal((JsonValueKind.Null, true, true, false, true), (kct.GetProperty("percent").ValueKind, kct.GetProperty("isExempt").GetBoolean(), kct.GetProperty("isSystem").GetBoolean(), kct.GetProperty("isDefault").GetBoolean(), kct.GetProperty("active").GetBoolean()));
        Assert.Equal([0m, 5m, 8m, 10m], rates.Skip(1).Select(r => r.Money("percent")));
        Assert.Equal(["10%"], rates.Where(r => r.GetProperty("isDefault").GetBoolean()).Select(r => r.GetProperty("name").GetString()));
        Assert.All(rates.Skip(1), r => Assert.False(r.GetProperty("isSystem").GetBoolean() || r.GetProperty("isExempt").GetBoolean()));
        Assert.Equal(VatRateIds.Kct, kct.Id());
        // Seed items have KCT, so it counts items.
        await owner.CreateItemAsync("Seed");
        Assert.True((await RateAsync(owner, VatRateIds.Kct)).GetProperty("itemCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Staff_who_view_receipts_read_the_list_and_only_the_Owner_writes_it()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var (zone, _) = await api.ZoneWithRaftAsync();
        var manager = await api.ZoneManagerAsync(zone);
        var accountant = await api.AccountantAsync();
        var five = await RateAsync(owner, VatRateIds.Five);

        foreach (var reader in new[] { manager, accountant })
        {
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Rates)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Rates}?active=true")).StatusCode);
            await (await reader.PostAsJsonAsync(Rates, new { name = "6%", percent = 6 })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await reader.PutAsJsonAsync($"{Rates}/{VatRateIds.Five}", new { name = "5%", percent = 6, rowVersion = Version(five) })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await reader.PostAsync($"{Rates}/{VatRateIds.Eight}/default", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await reader.PostAsJsonAsync($"{Rates}/{VatRateIds.Five}/deactivate", new { rowVersion = Version(five) })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await reader.PostAsync($"{Rates}/{VatRateIds.Five}/activate", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        Assert.Equal(5m, (await RateAsync(owner, VatRateIds.Five)).Money("percent"));
    }

    [Fact]
    public async Task A_new_rate_is_listed_with_a_unique_name_that_ignores_letter_case_and_a_valid_percent()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var created = await (await owner.PostAsJsonAsync(Rates, new { name = "Giảm 2%", percent = 2.5 })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("Giảm 2%", 2.5m, true, false, false, 0), (created.GetProperty("name").GetString(), created.Money("percent"), created.GetProperty("active").GetBoolean(), created.GetProperty("isSystem").GetBoolean(), created.GetProperty("isDefault").GetBoolean(), created.GetProperty("itemCount").GetInt32()));
        var duplicate = await owner.PostAsJsonAsync(Rates, new { name = "giảm 2%", percent = 3 });
        var problem = await duplicate.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((created.Id(), "Name"), (problem.GetProperty("existingId").GetGuid(), problem.GetProperty("field").GetString()));
        await (await owner.PostAsJsonAsync(Rates, new { name = "kct", percent = 3 })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal("Name", await (await owner.PostAsJsonAsync(Rates, new { name = "  ", percent = 3 })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Name", await (await owner.PostAsJsonAsync(Rates, new { name = new string('x', 51), percent = 3 })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Percent", await (await owner.PostAsJsonAsync(Rates, new { name = "Quá 100", percent = 100.5 })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Percent", await (await owner.PostAsJsonAsync(Rates, new { name = "Âm", percent = -1 })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Percent", await (await owner.PostAsJsonAsync(Rates, new { name = "Ba số lẻ", percent = 1.234 })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Percent", await (await owner.PostAsJsonAsync(Rates, new { name = "Không %" })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task Editing_a_rate_changes_its_percent_for_the_documents_made_afterwards_and_a_stale_screen_is_refused()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var ten = await RateAsync(owner, VatRateIds.Ten);

        var edited = await (await owner.PutAsJsonAsync($"{Rates}/{VatRateIds.Ten}", new { name = "10%", percent = 8, rowVersion = Version(ten) })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(8m, edited.Money("percent"));
        Assert.Equal(8m, (await RateAsync(owner, VatRateIds.Ten)).Money("percent"));
        // The first screen is stale now.
        await (await owner.PutAsJsonAsync($"{Rates}/{VatRateIds.Ten}", new { name = "10%", percent = 7, rowVersion = Version(ten) })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        // A name another rate has is refused.
        var clash = await (await owner.PutAsJsonAsync($"{Rates}/{VatRateIds.Ten}", new { name = "5%", percent = 8, rowVersion = Version(edited) })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal(VatRateIds.Five, clash.GetProperty("existingId").GetGuid());
        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == VatRateIds.Ten && e.Action == "VatRate.Updated"));
        Assert.Contains("10", audit.Before);
        Assert.Contains("8", audit.After);
    }

    [Fact]
    public async Task KCT_cannot_be_edited_deactivated_or_made_the_default()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var kct = await RateAsync(owner, VatRateIds.Kct);

        var edit = await (await owner.PutAsJsonAsync($"{Rates}/{VatRateIds.Kct}", new { name = "KCT", percent = 5, rowVersion = Version(kct) })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var deactivate = await (await owner.PostAsJsonAsync($"{Rates}/{VatRateIds.Kct}/deactivate", new { replacementId = VatRateIds.Ten, rowVersion = Version(kct) })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var makeDefault = await (await owner.PostAsync($"{Rates}/{VatRateIds.Kct}/default", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.All(new[] { edit, deactivate, makeDefault }, p => Assert.False(string.IsNullOrEmpty(p.GetProperty("reasonCode").GetString())));
        var after = await RateAsync(owner, VatRateIds.Kct);
        Assert.Equal(("KCT", JsonValueKind.Null, true), (after.GetProperty("name").GetString(), after.GetProperty("percent").ValueKind, after.GetProperty("active").GetBoolean()));
    }

    [Fact]
    public async Task Making_another_rate_the_default_leaves_one_default_and_a_new_supply_item_starts_at_it()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var made = await (await owner.PostAsync($"{Rates}/{VatRateIds.Eight}/default", null)).ReadAsync(HttpStatusCode.OK);

        Assert.True(made.GetProperty("isDefault").GetBoolean());
        var rates = (await owner.GetFromJsonAsync<JsonElement>(Rates)).EnumerateArray().ToList();
        Assert.Equal([VatRateIds.Eight], rates.Where(r => r.GetProperty("isDefault").GetBoolean()).Select(r => r.Id()));
        var item = await (await owner.PostAsJsonAsync("/api/farming-input-items", new { code = NewCode(), name = $"Phao {NewCode()}", group = "Material", unit = "cái" })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(VatRateIds.Eight, item.GetProperty("purchaseVatRateId").GetGuid());
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "VatRate.DefaultChanged" && e.RecordId == VatRateIds.Eight)));
    }

    [Fact]
    public async Task The_default_cannot_be_deactivated_and_an_inactive_rate_cannot_become_the_default()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var ten = await RateAsync(owner, VatRateIds.Ten);

        await (await owner.PostAsJsonAsync($"{Rates}/{VatRateIds.Ten}/deactivate", new { replacementId = VatRateIds.Eight, rowVersion = Version(ten) })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.True((await RateAsync(owner, VatRateIds.Ten)).GetProperty("active").GetBoolean());

        var zero = await RateAsync(owner, VatRateIds.Zero);
        await (await owner.PostAsJsonAsync($"{Rates}/{VatRateIds.Zero}/deactivate", new { rowVersion = Version(zero) })).ReadAsync(HttpStatusCode.OK);
        await (await owner.PostAsync($"{Rates}/{VatRateIds.Zero}/default", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task Deactivating_a_rate_that_items_have_needs_a_replacement_and_moves_them_there()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var five = await RateAsync(owner, VatRateIds.Five);
        var a = await owner.CreateItemAsync("Material", "cái", vatRate: "5");
        var b = await owner.CreateItemAsync("Other", "m", vatRate: "5");
        Assert.Equal(2, (await RateAsync(owner, VatRateIds.Five)).GetProperty("itemCount").GetInt32());

        // No replacement, itself, an unknown rate and an inactive one are all refused on replacementId.
        var zero = await RateAsync(owner, VatRateIds.Zero);
        await (await owner.PostAsJsonAsync($"{Rates}/{VatRateIds.Zero}/deactivate", new { rowVersion = Version(zero) })).ReadAsync(HttpStatusCode.OK);
        foreach (var replacement in new Guid?[] { null, VatRateIds.Five, Guid.NewGuid(), VatRateIds.Zero })
        {
            var refused = await owner.PostAsJsonAsync($"{Rates}/{VatRateIds.Five}/deactivate", new { replacementId = replacement, rowVersion = Version(five) });
            Assert.Equal("replacementId", await refused.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        Assert.True((await RateAsync(owner, VatRateIds.Five)).GetProperty("active").GetBoolean());

        var done = await (await owner.PostAsJsonAsync($"{Rates}/{VatRateIds.Five}/deactivate", new { replacementId = VatRateIds.Eight, rowVersion = Version(five) })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(2, done.GetProperty("movedItems").GetInt32());
        Assert.False(done.GetProperty("rate").GetProperty("active").GetBoolean());
        foreach (var id in new[] { a, b })
        {
            Assert.Equal("8%", (await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{id}")).GetProperty("purchaseVatRateName").GetString());
        }

        // Inactive rates stay in the list, after the active ones, and the active filter leaves them out.
        var all = (await owner.GetFromJsonAsync<JsonElement>(Rates)).EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.Equal(["KCT", "8%", "10%", "0%", "5%"], all);
        var active = (await owner.GetFromJsonAsync<JsonElement>($"{Rates}?active=true")).EnumerateArray().Select(r => r.GetProperty("name").GetString());
        Assert.Equal(["KCT", "8%", "10%"], active);
        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == VatRateIds.Five && e.Action == "VatRate.Deactivated"));
        Assert.Contains("\"MovedItems\":2", audit.After);
    }

    [Fact]
    public async Task An_inactive_rate_is_activated_again_and_audited()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var zero = await RateAsync(owner, VatRateIds.Zero);
        await (await owner.PostAsJsonAsync($"{Rates}/{VatRateIds.Zero}/deactivate", new { rowVersion = Version(zero) })).ReadAsync(HttpStatusCode.OK);

        var back = await (await owner.PostAsync($"{Rates}/{VatRateIds.Zero}/activate", null)).ReadAsync(HttpStatusCode.OK);

        Assert.True(back.GetProperty("active").GetBoolean());
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "VatRate.Activated" && e.RecordId == VatRateIds.Zero)));
        await (await owner.PostAsync($"{Rates}/{Guid.NewGuid()}/activate", null)).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task A_receipt_line_names_an_active_rate_only_and_an_unknown_one_is_refused()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var phao = await world.Owner.CreateItemAsync("Material", "cái", vatRate: "10");
        var unknown = await world.Owner.PostReceiptAsync(world.Body(new LineBody(phao, 1, 100_000, VatRateId: Guid.NewGuid())) with { Complete = true });
        Assert.Equal("Line 1 VAT rate", await unknown.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var zero = await RateAsync(world.Owner, VatRateIds.Zero);
        await (await world.Owner.PostAsJsonAsync($"{Rates}/{VatRateIds.Zero}/deactivate", new { rowVersion = Version(zero) })).ReadAsync(HttpStatusCode.OK);
        var inactive = await world.Owner.PostReceiptAsync(world.Body(new LineBody(phao, 1, 100_000, VatRateId: VatRateIds.Zero)) with { Complete = true });
        Assert.Equal("Line 1 VAT rate", await inactive.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }
}
