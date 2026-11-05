using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-07 of SRS 27/10: optional address and email (format checked), edit only while In Use (MSG-GEN-09), Resume,
/// and the role-only access of records with no zone: the Zone Manager maintains suppliers, the Accountant views them,
/// and farming input items (PURCHASE-08) are written by the Cooperative Owner only.
/// </summary>
public class SupplierSrsRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Suppliers = "/api/suppliers";

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("thu@example")]
    [InlineData("thu@@example.com")]
    [InlineData("thu nguyen@example.com")]
    public async Task An_email_in_a_wrong_format_returns_MSG_GEN_01_naming_the_email_and_saves_nothing(string email)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();

        var response = await owner.PostAsJsonAsync(Suppliers, new { name = "Trại giống", supplierType = "Seed", phoneNumber = phone, email });

        Assert.Equal("Email", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.False(await factory.QueryAsync(db => db.Suppliers.AnyAsync(s => s.PhoneNumber == phone)));
    }

    [Fact]
    public async Task Address_and_email_are_optional_and_the_email_is_kept_trimmed_in_lower_case()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var bare = await (await owner.PostAsJsonAsync(Suppliers, new { name = "Đại lý Quan Lạn", supplierType = "Other", phoneNumber = NewPhone() }))
            .ReadAsync(HttpStatusCode.Created);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (bare.GetProperty("address").ValueKind, bare.GetProperty("email").ValueKind));

        var full = await (await owner.PostAsJsonAsync(Suppliers, new
        {
            name = "Trại giống Cái Rồng", supplierType = "Seed", phoneNumber = NewPhone(), address = "  Cái Rồng, Vân Đồn  ", email = "  Thu.Nguyen@Example.COM ",
        })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(("Cái Rồng, Vân Đồn", "thu.nguyen@example.com"), (full.GetProperty("address").GetString(), full.GetProperty("email").GetString()));

        // Emptied on edit.
        var cleared = await (await owner.PutAsJsonAsync($"{Suppliers}/{full.Id()}", new
        {
            name = "Trại giống Cái Rồng", supplierType = "Seed", phoneNumber = full.GetProperty("phoneNumber").GetString(), address = "", email = (string?)null,
            rowVersion = full.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (cleared.GetProperty("address").ValueKind, cleared.GetProperty("email").ValueKind));
        var updated = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == full.Id() && e.Action == "Supplier.Updated"));
        Assert.Contains("thu.nguyen@example.com", updated.Before);
    }

    [Fact]
    public async Task A_discontinued_supplier_is_not_edited_and_gets_no_certificate_until_it_is_resumed()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var id = await owner.CreateSupplierAsync("Material");
        var view = await owner.GetFromJsonAsync<JsonElement>($"{Suppliers}/{id}");
        object Body(string name) => new
        {
            name, supplierType = "Material", phoneNumber = view.GetProperty("phoneNumber").GetString(), rowVersion = view.GetProperty("rowVersion").GetString(),
        };
        await (await owner.PostAsync($"{Suppliers}/{id}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        var problem = await (await owner.PutAsJsonAsync($"{Suppliers}/{id}", Body("Renamed"))).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal("Supplier is Discontinued and cannot be used for this action.", problem.GetProperty("detail").GetString());
        await (await owner.DeleteAsync($"{Suppliers}/{id}/certificates/0")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        using var form = new MultipartFormDataContent();
        var pdf = new ByteArrayContent("%PDF-1.4\n"u8.ToArray());
        pdf.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "files", "vietgap.pdf");
        await (await owner.PostAsync($"{Suppliers}/{id}/certificates", form)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.NotEqual("Renamed", (await factory.QueryAsync(db => db.Suppliers.SingleAsync(s => s.Id == id))).Name);

        var resumed = await (await owner.PostAsync($"{Suppliers}/{id}/resume", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("In Use", resumed.GetProperty("status").GetString());
        await (await owner.PostAsync($"{Suppliers}/{id}/resume", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await owner.PutAsJsonAsync($"{Suppliers}/{id}", Body("Renamed"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Supplier.Created", "Supplier.Discontinued", "Supplier.Reactivated", "Supplier.Updated"], await factory.AuditActionsAsync(id));
    }

    [Fact]
    public async Task Zone_managers_maintain_every_supplier_the_accountant_views_them_and_only_the_owner_writes_items()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (zoneA, _) = await factory.ZoneWithRaftAsync();
        var (zoneB, _) = await factory.ZoneWithRaftAsync();
        var managerA = await factory.ZoneManagerAsync(zoneA);
        var managerB = await factory.ZoneManagerAsync(zoneB);
        // A pair in one zone is enough: suppliers have no zone and follow the role only (BR-FARM-08).
        var accountant = await factory.AccountantAsync(zoneB);

        // A supplier added by the Zone Manager of zone A is maintained by the Zone Manager of zone B.
        var id = await managerA.CreateSupplierAsync("Seed");
        var view = await managerB.GetFromJsonAsync<JsonElement>($"{Suppliers}/{id}");
        await (await managerB.PutAsJsonAsync($"{Suppliers}/{id}", new
        {
            name = "Trại giống Bạch Long", supplierType = "Seed", phoneNumber = view.GetProperty("phoneNumber").GetString(), address = "Cát Hải",
            rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        await (await managerB.PostAsync($"{Suppliers}/{id}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        await (await managerA.PostAsync($"{Suppliers}/{id}/resume", null)).ReadAsync(HttpStatusCode.OK);

        // The Accountant views suppliers (payee of a supplier payment) and maintains none.
        Assert.Contains(
            (await (await accountant.GetAsync($"{Suppliers}?pageSize=100&search={Uri.EscapeDataString("Bạch Long")}")).ReadAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray(),
            s => s.Id() == id);
        await (await accountant.GetAsync($"{Suppliers}/{id}")).ReadAsync(HttpStatusCode.OK);
        await (await accountant.PostAsJsonAsync(Suppliers, new { name = "X", supplierType = "Seed", phoneNumber = NewPhone() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.PutAsJsonAsync($"{Suppliers}/{id}", new { name = "X", supplierType = "Seed", phoneNumber = NewPhone(), rowVersion = "" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.PostAsync($"{Suppliers}/{id}/discontinue", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.PostAsync($"{Suppliers}/{id}/resume", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        // Farming input items: the Cooperative Owner only (PURCHASE-08), the staff read them for their pickers.
        foreach (var staff in new[] { managerA, accountant })
        {
            await (await staff.PostAsJsonAsync("/api/farming-input-items", new { code = NewCode(), name = $"X {NewCode()}", group = "Other", unit = "kg" }))
                .ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await staff.GetAsync("/api/farming-input-items")).ReadAsync(HttpStatusCode.OK);
        }

        await owner.CreateItemAsync("Other");
        Assert.Equal(Domain.Constants.StatusIds.Usage.InUse, (await factory.QueryAsync(db => db.Suppliers.SingleAsync(s => s.Id == id))).StatusId);
    }
}
