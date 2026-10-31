using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PR P1 (OMTS-218), the purchasing catalogue of SRS 27/10: PURCHASE-07 and PURCHASE-08 against the §I.4.4 matrix
/// (anonymous, trader, Accountant, revoked pair), GBR-01 on discontinued records (read-only, resumed, payments still
/// recorded), GBR-03 proposals and the edit of a record against its own code or name. Races and the migration are in
/// <see cref="PurchaseCatalogueSqlServerQaTests"/>.
/// </summary>
public class PurchaseCatalogueQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Items = "/api/farming-input-items";
    private const string Suppliers = "/api/suppliers";
    private static readonly byte[] Pdf = "%PDF-1.4\n%certificate\n"u8.ToArray();

    /// <summary>Every endpoint of both catalogues, with a body where one is needed.</summary>
    private static IEnumerable<(HttpMethod Method, string Path, Func<HttpContent?> Body)> Endpoints(Guid supplierId, Guid itemId)
    {
        HttpContent Json(object body) => JsonContent.Create(body);
        object SupplierBody() => new { name = "X", supplierType = "Seed", phoneNumber = NewPhone() };
        object ItemBody() => new { code = NewCode(), name = $"X {NewCode()}", group = "Other", unit = "kg" };

        yield return (HttpMethod.Get, Suppliers, () => null);
        yield return (HttpMethod.Get, $"{Suppliers}/{supplierId}", () => null);
        yield return (HttpMethod.Post, Suppliers, () => Json(SupplierBody()));
        yield return (HttpMethod.Put, $"{Suppliers}/{supplierId}", () => Json(SupplierBody()));
        yield return (HttpMethod.Post, $"{Suppliers}/{supplierId}/discontinue", () => null);
        yield return (HttpMethod.Post, $"{Suppliers}/{supplierId}/resume", () => null);
        yield return (HttpMethod.Post, $"{Suppliers}/{supplierId}/reactivate", () => null);
        yield return (HttpMethod.Post, $"{Suppliers}/{supplierId}/certificates", () => Form("vietgap.pdf"));
        yield return (HttpMethod.Delete, $"{Suppliers}/{supplierId}/certificates/0", () => null);
        yield return (HttpMethod.Get, Items, () => null);
        yield return (HttpMethod.Get, $"{Items}/next-code", () => null);
        yield return (HttpMethod.Get, $"{Items}/{itemId}", () => null);
        yield return (HttpMethod.Post, Items, () => Json(ItemBody()));
        yield return (HttpMethod.Put, $"{Items}/{itemId}", () => Json(ItemBody()));
        yield return (HttpMethod.Post, $"{Items}/{itemId}/discontinue", () => null);
        yield return (HttpMethod.Post, $"{Items}/{itemId}/resume", () => null);
        yield return (HttpMethod.Post, $"{Items}/{itemId}/reactivate", () => null);
    }

    [Fact]
    public async Task Anonymous_callers_get_401_and_traders_or_system_administrators_MSG_GEN_08_on_every_supplier_and_item_endpoint()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var supplierId = await owner.CreateSupplierAsync();
        var itemId = await owner.CreateItemAsync("Other");
        var anonymous = factory.CreateClient();
        // §I.4.4: PURCHASE-07 and PURCHASE-08 are No for the trader; the System Administrator has no business rights.
        var refusedCallers = new[]
        {
            await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username),
            await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password, SubjectType.SystemAdministrator)).Username),
        };

        foreach (var (method, path, body) in Endpoints(supplierId, itemId))
        {
            using var noToken = await anonymous.SendAsync(new HttpRequestMessage(method, path) { Content = body() });
            Assert.True(noToken.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path}: {(int)noToken.StatusCode}");

            foreach (var caller in refusedCallers)
            {
                using var refused = await caller.SendAsync(new HttpRequestMessage(method, path) { Content = body() });
                Assert.True(await refused.CodeOrStatusAsync() == "403 MSG-GEN-08", $"{method} {path}: {await refused.CodeOrStatusAsync()}");
            }
        }

        // Nothing changed: both records are as the Owner created them.
        Assert.Equal(StatusIds.Usage.InUse, (await factory.QueryAsync(db => db.Suppliers.SingleAsync(s => s.Id == supplierId))).StatusId);
        Assert.Equal(StatusIds.Usage.InUse, (await factory.QueryAsync(db => db.FarmingInputItems.SingleAsync(i => i.Id == itemId))).StatusId);
    }

    [Fact]
    public async Task The_accountant_reads_suppliers_but_never_touches_their_certificates_nor_any_item()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var supplierId = await owner.CreateSupplierAsync();
        await (await owner.PostAsync($"{Suppliers}/{supplierId}/certificates", Form("vietgap.pdf"))).ReadAsync(HttpStatusCode.OK);
        var itemId = await owner.CreateItemAsync("Other");
        var (zone, _) = await factory.ZoneWithRaftAsync();

        foreach (var accountant in new[] { await factory.AccountantAsync(), await factory.AccountantAsync(zone) })
        {
            await (await accountant.GetAsync($"{Suppliers}/{supplierId}")).ReadAsync(HttpStatusCode.OK);
            await (await accountant.GetAsync($"{Items}/{itemId}")).ReadAsync(HttpStatusCode.OK);
            await (await accountant.PostAsync($"{Suppliers}/{supplierId}/certificates", Form("other.pdf"))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await accountant.DeleteAsync($"{Suppliers}/{supplierId}/certificates/0")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await accountant.PostAsync($"{Suppliers}/{supplierId}/reactivate", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await accountant.PostAsync($"{Items}/{itemId}/discontinue", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        var certificates = await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == supplierId).Select(s => s.CertificateUrls).SingleAsync());
        Assert.Single(certificates);
    }

    [Fact]
    public async Task A_revoked_zone_manager_pair_gives_no_right_on_suppliers()
    {
        var (zone, _) = await factory.ZoneWithRaftAsync();
        var (otherZone, _) = await factory.ZoneWithRaftAsync();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        // The Accountant pair lets the account log in and read suppliers; the Zone Manager pair is revoked (FARM-05.1).
        await factory.GrantAsync(staff, StaffRole.Accountant, zone);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, otherZone, g =>
        {
            g.StatusId = StatusIds.AccessGrant.Revoked;
            g.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            g.RevokeReason = "Moved to the office";
        });
        var client = await factory.LoginAsync(staff.Username);
        var owner = await factory.PurchasingOwnerAsync();
        var supplierId = await owner.CreateSupplierAsync();

        await (await client.GetAsync($"{Suppliers}/{supplierId}")).ReadAsync(HttpStatusCode.OK);
        var phone = NewPhone();
        await (await client.PostAsJsonAsync(Suppliers, new { name = "Trại giống", supplierType = "Seed", phoneNumber = phone })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await client.PostAsync($"{Suppliers}/{supplierId}/discontinue", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.False(await factory.QueryAsync(db => db.Suppliers.AnyAsync(s => s.PhoneNumber == phone)));
    }

    [Fact]
    public async Task Zone_managers_of_any_zone_keep_the_certificates_of_a_supplier_the_owner_added()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var supplierId = await owner.CreateSupplierAsync("Material");
        var (zoneA, _) = await factory.ZoneWithRaftAsync();
        var (zoneB, _) = await factory.ZoneWithRaftAsync();
        var managerA = await factory.ZoneManagerAsync(zoneA);
        var managerB = await factory.ZoneManagerAsync(zoneB);

        // The supplier has no zone (BR-PURCHASE-06): a pair in any zone is enough (BR-FARM-08).
        var added = await (await managerA.PostAsync($"{Suppliers}/{supplierId}/certificates", Form("vietgap.pdf"))).ReadAsync(HttpStatusCode.OK);
        Assert.Single(added.GetProperty("certificates").EnumerateArray());
        var removed = await (await managerB.DeleteAsync($"{Suppliers}/{supplierId}/certificates/0")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(removed.GetProperty("certificates").EnumerateArray());

        Assert.Equal(["Supplier.Created", "Supplier.Updated", "Supplier.Updated"], await factory.AuditActionsAsync(supplierId));
    }

    [Fact]
    public async Task A_discontinued_supplier_keeps_its_phone_stays_readable_and_still_receives_payments()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 2, 100_000));
        var phone = (await world.Owner.GetFromJsonAsync<JsonElement>($"{Suppliers}/{world.SupplierId}")).GetProperty("phoneNumber").GetString()!;
        await (await world.Owner.PostAsync($"{Suppliers}/{world.SupplierId}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        // PURCHASE-07.0.E2: the phone of a Discontinued supplier stays taken, on create and on the edit of another one.
        var again = await world.Owner.PostAsJsonAsync(Suppliers, new { name = "Copy", supplierType = "Seed", phoneNumber = $"+84{phone[1..]}" });
        Assert.Equal(world.SupplierId, (await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
        var other = await world.Owner.CreateSupplierAsync();
        var otherView = await world.Owner.GetFromJsonAsync<JsonElement>($"{Suppliers}/{other}");
        var edit = await world.Owner.PutAsJsonAsync($"{Suppliers}/{other}", new
        {
            name = "Other", supplierType = "Seed", phoneNumber = phone, rowVersion = otherView.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal(world.SupplierId, (await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());

        // GBR-01: it can still be viewed and listed with its status.
        var accountant = await factory.AccountantAsync();
        Assert.Equal("Discontinued", (await (await accountant.GetAsync($"{Suppliers}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK)).GetProperty("status").GetString());
        var discontinued = await world.Owner.GetFromJsonAsync<JsonElement>($"{Suppliers}?search={phone}&statusId={StatusIds.Usage.Discontinued}");
        Assert.Equal([world.SupplierId], discontinued.GetProperty("items").EnumerateArray().Select(s => s.Id()));

        // POST-2 of PURCHASE-07, BR-PURCHASE-06: the Accountant can still record a payment to it.
        var paid = await accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 50_000, Method = "Cash" });
        await paid.ReadAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_discontinued_item_is_read_only_even_unchanged_and_is_offered_again_once_resumed()
    {
        var world = await factory.WorldAsync();
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"{Items}/{world.Other}");
        object Same() => new
        {
            code = view.GetProperty("code").GetString(), name = view.GetProperty("name").GetString(), group = "Other", unit = "bag",
            rowVersion = view.GetProperty("rowVersion").GetString(),
        };
        await (await world.Owner.PostAsync($"{Items}/{world.Other}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        // GBR-01: viewed, never edited, not even with the values it already holds.
        Assert.Equal("Discontinued", (await world.Owner.GetFromJsonAsync<JsonElement>($"{Items}/{world.Other}")).GetProperty("status").GetString());
        await (await world.Owner.PutAsJsonAsync($"{Items}/{world.Other}", Same())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var refused = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // PURCHASE-08.2: resumed with the same code and history, then offered on new receipts again.
        var resumed = await (await world.Owner.PostAsync($"{Items}/{world.Other}/resume", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((view.GetProperty("code").GetString(), "In Use"), (resumed.GetProperty("code").GetString(), resumed.GetProperty("status").GetString()));
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        Assert.Equal(["FarmingInputItem.Created", "FarmingInputItem.Discontinued", "FarmingInputItem.Reactivated"], await factory.AuditActionsAsync(world.Other));
    }

    [Fact]
    public async Task An_item_may_keep_or_recase_its_own_code_and_name_and_a_used_item_audits_its_new_code()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(0, 0, new LineBody(world.Seed, 1, 10_000));
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"{Items}/{world.Seed}");
        var (code, name) = (view.GetProperty("code").GetString()!, view.GetProperty("name").GetString()!);

        // The uniqueness checks leave the item itself out (GBR-03, BR-PURCHASE-10).
        var recased = await (await world.Owner.PutAsJsonAsync($"{Items}/{world.Seed}", new
        {
            code = code.ToUpperInvariant(), name = name.ToUpperInvariant(), group = "Seed", unit = "kg", rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((code.ToUpperInvariant(), name.ToUpperInvariant(), true), (recased.GetProperty("code").GetString(), recased.GetProperty("name").GetString(), recased.GetProperty("isUsed").GetBoolean()));

        // A used item may take another code (GBR-03); the audit keeps the code before and after (GBR-02).
        var newCode = $"VT-{NewCode()}";
        await (await world.Owner.PutAsJsonAsync($"{Items}/{world.Seed}", new
        {
            code = newCode, name = name.ToUpperInvariant(), group = "Seed", unit = "kg", rowVersion = recased.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        var updates = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == world.Seed && e.Action == "FarmingInputItem.Updated").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(2, updates.Count);
        Assert.Contains(code.ToUpperInvariant(), updates[1].Before);
        Assert.Contains(newCode, updates[1].After);
        var receipt = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?itemId={world.Seed}&zoneId={world.Zone.Id}");
        Assert.Equal(1, receipt.GetProperty("page").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_proposal_skips_codes_outside_the_pattern_grows_past_VT999_and_follows_a_used_item_recoded()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        foreach (var code in new[] { "VT-050", "AVT070", "VT 080", "VTX", "VT1A" })
        {
            await owner.CreateItemAsync("Other", code: code);
        }

        Assert.Equal("VT001", await ProposedAsync(owner));

        // The Owner replaces the proposal with a code of the pattern: the next proposal follows it.
        var item = await owner.CreateItemAsync("Other", code: "VT999");
        Assert.Equal("VT1000", await ProposedAsync(owner));
        await owner.CreateItemAsync("Other", code: await ProposedAsync(owner));
        Assert.Equal("VT1001", await ProposedAsync(owner));

        // The code of a used item changes (GBR-03): the old number is free for the proposal, the new one is counted.
        var view = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}");
        await (await owner.PutAsJsonAsync($"{Items}/{item}", new
        {
            code = "vt2000", name = view.GetProperty("name").GetString(), group = "Other", unit = "kg", rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("VT2001", await ProposedAsync(owner));
    }

    [Fact]
    public async Task The_old_group_name_is_refused_in_filters_and_Material_filters_receipts_and_raft_costs()
    {
        var world = await factory.WorldAsync();
        var material = await world.CompletedAsync(0, 0, new LineBody(world.Material, 2, 50_000));
        await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 70_000));

        // An old client filtering on RaftMaterial gets MSG-GEN-01 instead of an unfiltered list.
        await (await world.Owner.GetAsync($"{Items}?group=RaftMaterial")).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await world.Owner.GetAsync($"/api/purchase-receipts?itemGroup=RaftMaterial&zoneId={world.Zone.Id}")).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        var receipts = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?itemGroup=Material&zoneId={world.Zone.Id}");
        Assert.Equal([material.Id()], receipts.GetProperty("page").GetProperty("items").EnumerateArray().Select(r => r.Id()));
        var costs = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/raft-material-costs?raftId={world.Raft.Id}");
        Assert.Equal(100_000, costs.EnumerateArray().Single().Money("materialCost"));
    }

    private static async Task<string> ProposedAsync(HttpClient owner) =>
        (await (await owner.GetAsync($"{Items}/next-code")).ReadAsync(HttpStatusCode.OK)).GetProperty("code").GetString()!;

    private static MultipartFormDataContent Form(string fileName)
    {
        var form = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(Pdf);
        pdf.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "files", fileName);
        return form;
    }
}
