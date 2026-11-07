using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223, SALE-11: price rules, malformed input, message texts, state rules.</summary>
public class SalesCatalogueQaProductTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    public static TheoryData<string> Malformed => new()
    {
        "", "null", "[]", "123", "\"x\"", "{", "{\"name\":123,\"unitPrice\":1}", "{\"name\":\"\\ud83d\",\"unitPrice\":1}", "{\"name\":\"X\",\"unitPrice\":true}",
        "{\"name\":\"X\",\"unitPrice\":1e400}", "{\"name\":\"X\",\"unitPrice\":\"NaN\"}", "{\"name\":\"X\",\"unitPrice\":[1]}", "{\"name\":{\"a\":1},\"unitPrice\":1}",
        "{\"name\":\"X\",\"unitPrice\":1,\"rowVersion\":123}",
    };

    // Never a 500 from malformed input: every one of these is a client error on both writes.
    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Malformed_product_bodies_are_client_errors_and_save_nothing(string body)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var before = await factory.QueryAsync(db => db.Products.CountAsync());

        var post = await owner.PostAsync("/api/products", Json(body));
        var put = await owner.PutAsync($"/api/products/{product.Id()}", Json(body));

        Assert.True((int)post.StatusCode is >= 400 and < 500, $"POST {(int)post.StatusCode}: {await post.Content.ReadAsStringAsync()}");
        Assert.True((int)put.StatusCode is >= 400 and < 500, $"PUT {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync()}");
        Assert.Equal(before, await factory.QueryAsync(db => db.Products.CountAsync()));
    }

    [Fact]
    public async Task A_body_of_another_media_type_is_a_client_error()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var response = await owner.PostAsync("/api/products", new StringContent("name=X", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    public static TheoryData<string> RefusedPrices => new()
    {
        "0", "-1", "0.0", "-0.5", "0.5", "1000.5", "60000.0001", "0.0000000000000000000000000001", "1000000000000000000", "79228162514264337593543950335", "1e30",
    };

    [Theory]
    [MemberData(nameof(RefusedPrices))]
    public async Task Prices_out_of_the_rule_are_msg_gen_01_on_create_and_edit(string price)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 60_000);

        var post = await owner.PostAsync("/api/products", Json($$"""{"name":"{{NewCode()}}","unitPrice":{{price}}}"""));
        var put = await owner.PutAsync($"/api/products/{product.Id()}", Json($$"""{"name":"{{NewCode()}}","unitPrice":{{price}},"rowVersion":"{{product.RowVersion()}}"}"""));

        await post.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await put.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal(60_000, (await (await owner.GetAsync($"/api/products/{product.Id()}")).ReadAsync(HttpStatusCode.OK)).GetProperty("unitPrice").GetDecimal());
    }

    // 60000.0 is the same price: no audit row and the stored price reads as whole VND.
    [Fact]
    public async Task A_price_with_a_zero_fraction_is_the_same_whole_price()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 60_000);

        var put = await owner.PutAsync($"/api/products/{product.Id()}", Json($$"""{"name":"{{product.Text("name")}}","unitPrice":60000.00,"rowVersion":"{{product.RowVersion()}}"}"""));

        await put.ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Product.Created"], await factory.AuditActionsAsync(product.Id()));
    }

    [Fact]
    public async Task The_message_texts_follow_the_srs_list()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var name = $"Hàu {NewCode()}";
        var product = await owner.CreateProductAsync(name);

        var duplicate = await (await owner.PostAsJsonAsync("/api/products", new { name, unitPrice = 1 })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal("Product already exists. Your entries are kept: change them, or open the existing record.", duplicate.Text("detail"));

        var invalid = await (await owner.PostAsJsonAsync("/api/products", new { name, unitPrice = 0 })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Unit price is missing or invalid. Please check and enter it again.", invalid.Text("detail"));

        var off = await (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Discontinued", off.Text("status"));
        var refused = await (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal($"Product {name} is Discontinued and cannot be used for this action.", refused.Text("detail"));

        var resumed = await (await owner.PostAsync($"/api/products/{product.Id()}/resume", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("In Use", resumed.Text("status"));
        Assert.Equal(name, resumed.Text("name"));
        var again = await (await owner.PostAsync($"/api/products/{product.Id()}/resume", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal($"Product {name} is In Use and cannot be used for this action.", again.Text("detail"));
    }

    // A rename keeps the old name out of the unique check, and the old name is free again.
    [Fact]
    public async Task A_renamed_product_frees_its_old_name_and_existing_id_opens_a_discontinued_product()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var oldName = $"Old {NewCode()}";
        var product = await owner.CreateProductAsync(oldName);

        var renamed = (await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = $"New {NewCode()}", unitPrice = 1, rowVersion = product.RowVersion() })).ReadAsync(HttpStatusCode.OK)).GetProperty("product");
        var second = await owner.CreateProductAsync(oldName);
        (await owner.PostAsync($"/api/products/{second.Id()}/discontinue", null)).EnsureSuccessStatusCode();

        var refused = await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = oldName, unitPrice = 1, rowVersion = renamed.RowVersion() })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        var opened = await (await owner.GetAsync($"/api/products/{refused.GetProperty("existingId").GetGuid()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.Usage.Discontinued, opened.GetProperty("statusId").GetInt32());
    }

    // A name and a price changed together: both, each with before and after, in one row.
    [Fact]
    public async Task A_name_and_price_change_is_one_audit_row_with_both_before_and_after()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 60_000);
        var newName = $"New {NewCode()}";

        await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = newName, unitPrice = 61_000, rowVersion = product.RowVersion() })).ReadAsync(HttpStatusCode.OK);

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == product.Id() && e.Action == "Product.Updated"));
        var (before, after) = (JsonDocument.Parse(audit.Before!).RootElement, JsonDocument.Parse(audit.After!).RootElement);
        Assert.Equal((product.Text("name"), 60_000m), (before.GetProperty("Name").GetString(), before.GetProperty("UnitPrice").GetDecimal()));
        Assert.Equal((newName, 61_000m), (after.GetProperty("Name").GetString(), after.GetProperty("UnitPrice").GetDecimal()));
        Assert.NotNull(audit.ActorAccountId);
        Assert.Equal(newName, audit.RecordCode);
    }

    // BR-SALE-04/05: "name mandatory" means a name a person can read.
    [Theory]
    [InlineData("\u200B")]
    [InlineData("\uFEFF")]
    [InlineData("\u2060")]
    public async Task L2_a_name_that_shows_nothing_is_msg_gen_01(string name)
    {
        var owner = await factory.PurchasingOwnerAsync();

        var product = await owner.PostAsJsonAsync("/api/products", new { name, unitPrice = 1 });
        var customer = await owner.PostAsJsonAsync("/api/customers", new { name, phoneNumber = NewPhone() });

        Assert.Equal(("400 MSG-GEN-01", "400 MSG-GEN-01"), (await product.CodeOrStatusAsync(), await customer.CodeOrStatusAsync()));
    }

    [Theory]
    [InlineData("\u00A0")]
    [InlineData("\u3000")]
    [InlineData("\t")]
    public async Task A_name_of_unicode_spaces_is_msg_gen_01(string name)
    {
        var owner = await factory.PurchasingOwnerAsync();

        await (await owner.PostAsJsonAsync("/api/products", new { name, unitPrice = 1 })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PostAsJsonAsync("/api/customers", new { name, phoneNumber = NewPhone() })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    // PM call pinned, not a defect: NFR-09 says each save sends a version, and the status actions take none. The siblings
    // (suppliers, input items) work the same; the design (§2.1) gives them an empty body.
    [Fact]
    public async Task Pinned_a_discontinue_from_an_old_screen_goes_through_with_no_version()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 60_000);
        await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 61_000, rowVersion = product.RowVersion() })).ReadAsync(HttpStatusCode.OK);

        await (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
    }
}
