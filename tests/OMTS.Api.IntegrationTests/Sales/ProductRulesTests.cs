using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-11, BR-SALE-05: list price above 0 in whole VND, unique name, edit, discontinue and resume, audit of the price.</summary>
public class ProductRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    public static TheoryData<string?, double?, string> InvalidEntries => new()
    {
        { null, 60_000, "Name" },
        { "   ", 60_000, "Name" },
        { new string('a', 201), 60_000, "Name" },
        { "Hàu", null, "Unit price" },
        { "Hàu", 0, "Unit price" },
        { "Hàu", -1, "Unit price" },
        { "Hàu", 1000.5, "Unit price" },
        { "Hàu", 1e19, "Unit price" },
    };

    [Theory]
    [MemberData(nameof(InvalidEntries))]
    public async Task Missing_or_invalid_entries_return_MSG_GEN_01_naming_the_field_and_save_nothing(string? name, double? unitPrice, string field)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var before = await factory.QueryAsync(db => db.Products.CountAsync());

        var response = await owner.PostAsJsonAsync("/api/products", new { name, unitPrice = (decimal?)unitPrice });

        Assert.Equal(field, await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(before, await factory.QueryAsync(db => db.Products.CountAsync()));
    }

    [Theory]
    [InlineData("\"sixty\"")]
    [InlineData("1e40")]
    public async Task A_price_that_cannot_be_read_is_refused_with_MSG_GEN_01(string price)
    {
        var owner = await factory.PurchasingOwnerAsync();
        using var content = new StringContent($$"""{ "name": "Hàu", "unitPrice": {{price}} }""", System.Text.Encoding.UTF8, "application/json");

        await (await owner.PostAsync("/api/products", content)).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task Several_invalid_entries_are_all_listed()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var body = await (await owner.PostAsJsonAsync("/api/products", new { name = "", unitPrice = 0 })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        Assert.Equal(["Name", "Unit price"], body.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task A_new_product_is_In_Use_with_MSG_GEN_05_and_an_audit_row()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var name = $"  Hàu sữa {NewCode()} ";

        var response = await owner.PostAsJsonAsync("/api/products", new { name, unitPrice = 60_000 });

        var body = await response.ReadAsync(HttpStatusCode.Created);
        Assert.Equal("MSG-GEN-05", body.Text("messageCode"));
        Assert.Equal("Product saved.", body.Text("message"));
        var product = body.GetProperty("product");
        Assert.Equal(name.Trim(), product.Text("name"));
        Assert.Equal(60_000, product.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(StatusIds.Usage.InUse, product.GetProperty("statusId").GetInt32());
        Assert.Equal(["Product.Created"], await factory.AuditActionsAsync(product.Id()));
    }

    // The audit row names the product by its code, so a long name never overflows the record code column.
    [Fact]
    public async Task A_name_of_200_characters_is_saved_and_audited()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var name = NewCode() + new string('ộ', 190);

        var product = await owner.CreateProductAsync(name);

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == product.Id()));
        Assert.Equal(product.Text("code"), audit.RecordCode);
    }

    // MSG-GEN-02 + existingId: the Owner may open the existing product, also a Discontinued one to resume it.
    [Fact]
    public async Task A_name_in_use_is_refused_ignoring_case_with_the_existing_id_discontinued_products_included()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var name = $"Hàu béo {NewCode()}";
        var first = await owner.CreateProductAsync(name);

        var same = await owner.PostAsJsonAsync("/api/products", new { name = $" {name.ToUpperInvariant()} ", unitPrice = 70_000 });
        Assert.Equal(first.Id(), (await same.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());

        (await owner.PostAsync($"/api/products/{first.Id()}/discontinue", null)).EnsureSuccessStatusCode();
        var discontinued = await owner.PostAsJsonAsync("/api/products", new { name, unitPrice = 70_000 });
        Assert.Equal(first.Id(), (await discontinued.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
    }

    [Fact]
    public async Task An_edit_may_keep_its_own_name_but_not_take_another_one()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var other = await owner.CreateProductAsync();
        var product = await owner.CreateProductAsync();

        var taken = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = other.Text("name"), unitPrice = 1, rowVersion = product.RowVersion() });
        Assert.Equal(other.Id(), (await taken.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());

        var own = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name")!.ToUpperInvariant(), unitPrice = 1, rowVersion = product.RowVersion() });
        await own.ReadAsync(HttpStatusCode.OK);
    }

    // SALE-11 POST-3: the audit log is the only price history.
    [Fact]
    public async Task A_price_change_is_audited_with_the_price_before_and_after()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 60_000);

        var body = await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 62_500, rowVersion = product.RowVersion() }))
            .ReadAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-GEN-05", body.Text("messageCode"));
        Assert.Equal(62_500, body.GetProperty("product").GetProperty("unitPrice").GetDecimal());
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == product.Id() && e.Action == "Product.Updated"));
        Assert.Equal(60_000, JsonDocument.Parse(audit.Before!).RootElement.GetProperty("UnitPrice").GetDecimal());
        Assert.Equal(62_500, JsonDocument.Parse(audit.After!).RootElement.GetProperty("UnitPrice").GetDecimal());
        Assert.False(JsonDocument.Parse(audit.After!).RootElement.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_no_audit_row()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();

        var response = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = product.GetProperty("unitPrice").GetDecimal(), rowVersion = product.RowVersion() });

        await response.ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Product.Created"], await factory.AuditActionsAsync(product.Id()));
    }

    [Fact]
    public async Task An_edit_needs_the_row_version_it_read()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();

        var missing = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = "New", unitPrice = 1 });
        Assert.Equal("RowVersion", await missing.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var stale = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = "New", unitPrice = 1, rowVersion = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) });
        await stale.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
    }

    [Fact]
    public async Task A_product_is_discontinued_and_resumed_with_its_price_and_history()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 75_000);

        var discontinued = await (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.Discontinued, discontinued.GetProperty("statusId").GetInt32());

        var resumed = await (await owner.PostAsync($"/api/products/{product.Id()}/resume", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.InUse, resumed.GetProperty("statusId").GetInt32());
        Assert.Equal(75_000, resumed.GetProperty("unitPrice").GetDecimal());

        Assert.Equal(["Product.Created", "Product.Discontinued", "Product.Resumed"], await factory.AuditActionsAsync(product.Id()));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == product.Id() && e.Action == "Product.Discontinued"));
        Assert.Contains("IN_USE", audit.Before);
        Assert.Contains("OUT_OF_USE", audit.After);
    }

    [Fact]
    public async Task A_status_action_on_the_wrong_status_or_an_edit_of_a_discontinued_product_returns_MSG_GEN_09()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();

        await (await owner.PostAsync($"/api/products/{product.Id()}/resume", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).EnsureSuccessStatusCode();
        await (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var edit = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = "New", unitPrice = 1, rowVersion = product.RowVersion() });
        var body = await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains(product.Text("name")!, body.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("discontinue")]
    [InlineData("resume")]
    public async Task An_unknown_product_returns_404(string action)
    {
        var owner = await factory.PurchasingOwnerAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/products/{Guid.NewGuid()}/{action}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/products/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/products/{Guid.NewGuid()}", new { name = "X", unitPrice = 1, rowVersion = "" })).StatusCode);
    }
}
