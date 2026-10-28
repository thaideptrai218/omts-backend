using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-02 rules and error bullets: Owner-only writes, readers, unique codes, fixed fields once used, pickers, audit.</summary>
public class FarmingInputItemRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(null, "Name", "Seed", "kg", "Code")]
    [InlineData("C", null, "Seed", "kg", "Name")]
    [InlineData("C", "Name", null, "kg", "Group")]
    [InlineData("C", "Name", "Seed", null, "Unit")]
    [InlineData("C", "Name", "Seed", "   ", "Unit")]
    public async Task Missing_code_name_group_or_unit_return_MSG01(string? code, string? name, string? group, string? unit, string field)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var response = await owner.PostAsJsonAsync("/api/farming-input-items", new { code = code is null ? null : $"{code}{NewCode()}", name, group, unit });
        Assert.Equal(field, await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task Too_long_values_return_MSG01()
    {
        var owner = await factory.PurchasingOwnerAsync();
        foreach (var (body, field) in new (object, string)[]
        {
            (new { code = new string('c', 51), name = "N", group = "Seed", unit = "kg" }, "Code"),
            (new { code = NewCode(), name = new string('n', 201), group = "Seed", unit = "kg" }, "Name"),
            (new { code = NewCode(), name = "N", group = "Seed", unit = new string('u', 31) }, "Unit"),
            (new { code = NewCode(), name = "N", group = "Seed", unit = "kg", description = new string('d', 2001) }, "Description"),
        })
        {
            Assert.Equal(field, await (await owner.PostAsJsonAsync("/api/farming-input-items", body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }
    }

    [Fact]
    public async Task A_code_that_exists_in_another_case_returns_MSG03_with_the_existing_id_on_create_and_edit()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var code = $"Seed-{NewCode()}";
        var first = await owner.CreateItemAsync("Seed", code: code);
        var duplicate = await owner.PostAsJsonAsync("/api/farming-input-items", new { code = code.ToUpperInvariant(), name = "Again", group = "Seed", unit = "kg" });
        Assert.Equal(first, (await duplicate.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());

        var second = await owner.CreateItemAsync("Other");
        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{second}");
        var edit = await owner.PutAsJsonAsync($"/api/farming-input-items/{second}", new
        {
            code = code.ToLowerInvariant(), name = "Other", group = "Other", unit = "kg", rowVersion = view.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal(first, (await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
    }

    [Theory]
    [InlineData("code", "Code")]
    [InlineData("group", "Group")]
    [InlineData("unit", "Unit")]
    public async Task A_used_item_keeps_code_group_and_unit_with_the_create_a_new_item_hint(string change, string field)
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(0, 0, new LineBody(world.Seed, 1, 10_000));
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{world.Seed}");
        Assert.True(view.GetProperty("isUsed").GetBoolean());
        var code = view.GetProperty("code").GetString();

        var response = await world.Owner.PutAsJsonAsync($"/api/farming-input-items/{world.Seed}", new
        {
            code = change == "code" ? $"{code}-NEW" : code,
            name = "Renamed seed",
            group = change == "group" ? "Other" : "Seed",
            unit = change == "unit" ? "piece" : "kg",
            rowVersion = view.GetProperty("rowVersion").GetString(),
        });

        var problem = await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal(field, problem.GetProperty("field").GetString());
        Assert.Equal("create a new item instead", problem.GetProperty("hint").GetString());
        var kept = await factory.QueryAsync(db => db.FarmingInputItems.SingleAsync(i => i.Id == world.Seed));
        Assert.Equal(("Seed item", code, "kg"), (kept.Name, kept.Code, kept.Unit));

        // The name and description can still change.
        await (await world.Owner.PutAsJsonAsync($"/api/farming-input-items/{world.Seed}", new
        {
            code, name = "Renamed seed", group = "Seed", unit = "kg", description = "Hàu Thái Bình Dương", rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_item_planned_by_a_cultivation_cycle_is_used_too()
    {
        var world = await factory.WorldAsync();
        await factory.UseDbAsync(db => db.CultivationCycles.Add(new CultivationCycle { RaftId = world.Raft.Id, OpenedAt = DateTimeOffset.UtcNow, PlannedSeedTypeId = world.Seed }));
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{world.Seed}");
        Assert.True(view.GetProperty("isUsed").GetBoolean());
        var change = await world.Owner.PutAsJsonAsync($"/api/farming-input-items/{world.Seed}", new
        {
            code = view.GetProperty("code").GetString(), name = "Seed", group = "Seed", unit = "con", rowVersion = view.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal("Unit", await change.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task An_unused_item_may_change_code_group_and_unit()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var id = await owner.CreateItemAsync("Seed");
        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{id}");
        var newCode = NewCode();
        var edited = await (await owner.PutAsJsonAsync($"/api/farming-input-items/{id}", new
        {
            code = newCode, name = "Rope 12 mm", group = "RaftMaterial", unit = "roll", rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((newCode, "RaftMaterial", "roll"), (edited.GetProperty("code").GetString(), edited.GetProperty("group").GetString(), edited.GetProperty("unit").GetString()));
    }

    [Fact]
    public async Task Every_write_is_owner_only_even_for_a_role_with_every_purchasing_action_everywhere()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var id = await owner.CreateItemAsync("Other");
        var staff = await factory.PurchasingStaffAsync(null,
            Perm.Full(PermissionFunctions.PurchaseReceipts), new Perm(PermissionFunctions.Suppliers, true, true, true),
            new Perm(PermissionFunctions.SupplierPayments, true, true, Export: true), Perm.Full(PermissionFunctions.CultivationCycles));

        await (await staff.PostAsJsonAsync("/api/farming-input-items", new { code = NewCode(), name = "X", group = "Other", unit = "kg" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await staff.PutAsJsonAsync($"/api/farming-input-items/{id}", new { code = NewCode(), name = "X", group = "Other", unit = "kg", rowVersion = "" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await staff.PostAsync($"/api/farming-input-items/{id}/discontinue", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await staff.PostAsync($"/api/farming-input-items/{id}/reactivate", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(StatusIds.Usage.InUse, (await factory.QueryAsync(db => db.FarmingInputItems.SingleAsync(i => i.Id == id))).StatusId);
    }

    [Fact]
    public async Task Readers_are_those_who_view_receipts_payments_or_cultivation_cycles()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var id = await owner.CreateItemAsync("Seed");
        var (zone, _) = await factory.ZoneWithRaftAsync();

        foreach (var reader in new[]
        {
            await factory.PurchasingStaffAsync(zone, new Perm(PermissionFunctions.PurchaseReceipts)),
            await factory.PurchasingStaffAsync(null, new Perm(PermissionFunctions.SupplierPayments)),
            await factory.PurchasingStaffAsync(zone, new Perm(PermissionFunctions.CultivationCycles)),
        })
        {
            await (await reader.GetAsync("/api/farming-input-items")).ReadAsync(HttpStatusCode.OK);
            await (await reader.GetAsync($"/api/farming-input-items/{id}")).ReadAsync(HttpStatusCode.OK);
        }

        foreach (var outsider in new[]
        {
            await factory.PurchasingStaffAsync(zone, new Perm(PermissionFunctions.Suppliers, true, true, true)),
            // SUPPLIER_PAYMENTS is cooperative-wide: a grant in one zone gives no view.
            await factory.PurchasingStaffAsync(zone, new Perm(PermissionFunctions.SupplierPayments)),
        })
        {
            await (await outsider.GetAsync("/api/farming-input-items")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await outsider.GetAsync($"/api/farming-input-items/{id}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }
    }

    // Done when: pickers return In Use items filtered by group.
    [Fact]
    public async Task Pickers_filter_by_group_and_status_and_discontinued_items_cannot_be_completed()
    {
        var world = await factory.WorldAsync();
        var oldSeed = await world.Owner.CreateItemAsync("Seed");
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(oldSeed, 5, 10_000)));
        await (await world.Owner.PostAsync($"/api/farming-input-items/{oldSeed}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.PostAsync($"/api/farming-input-items/{oldSeed}/discontinue", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        var picker = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items?group=Seed&statusId={StatusIds.Usage.InUse}");
        var ids = picker.EnumerateArray().Select(i => i.Id()).ToList();
        Assert.Contains(world.Seed, ids);
        Assert.DoesNotContain(oldSeed, ids);
        Assert.DoesNotContain(world.Material, ids);
        Assert.All(picker.EnumerateArray(), i => Assert.Equal(("Seed", "In Use"), (i.GetProperty("group").GetString(), i.GetProperty("status").GetString())));

        var complete = await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null);
        Assert.Equal("Line 1 item", await complete.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(0, await factory.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.SeedTypeId == oldSeed)));

        await (await world.Owner.PostAsync($"/api/farming-input-items/{oldSeed}/reactivate", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["FarmingInputItem.Created", "FarmingInputItem.Discontinued", "FarmingInputItem.Reactivated"], await factory.AuditActionsAsync(oldSeed));
    }

    [Fact]
    public async Task Edit_with_a_stale_row_version_returns_MSG06_and_audit_keeps_before_and_after()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var id = await owner.CreateItemAsync("Other");
        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{id}");
        await (await owner.PutAsJsonAsync($"/api/farming-input-items/{id}", new
        {
            code = view.GetProperty("code").GetString(), name = "Cage 60 cm", group = "Other", unit = "bag", rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        var updated = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == id && e.Action == "FarmingInputItem.Updated"));
        Assert.Contains("Other item", updated.Before);
        Assert.Contains("Cage 60 cm", updated.After);

        await factory.UpdateAsync<FarmingInputItem>(id, i => i.RowVersion = [0, 0, 0, 0, 0, 0, 9, 2]);
        var stale = await owner.PutAsJsonAsync($"/api/farming-input-items/{id}", new
        {
            code = view.GetProperty("code").GetString(), name = "Late change", group = "Other", unit = "bag", rowVersion = "AAAAAAAACQA=",
        });
        await stale.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal("Cage 60 cm", (await factory.QueryAsync(db => db.FarmingInputItems.SingleAsync(i => i.Id == id))).Name);
    }
}
