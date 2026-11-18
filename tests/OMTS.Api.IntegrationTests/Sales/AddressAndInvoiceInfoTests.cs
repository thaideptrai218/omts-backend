using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The address pickers and the e-invoice info of the customer and supplier forms: the administrative units, the tax code lookup (with the stubs of
/// <see cref="StubAddressServicesExtensions"/>, never the network), and the invoice rules per buyer type.
/// </summary>
public class AddressAndInvoiceInfoTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Dictionary<string, object?> Organization() => new()
    {
        ["name"] = $"Công ty {NewCode()}", ["phoneNumber"] = NewPhone(),
        ["provinceCode"] = 1, ["provinceName"] = "Thành phố Hà Nội", ["wardCode"] = 10, ["wardName"] = "Phường Cầu Giấy", ["street"] = "Số 5, Phố Nguyễn Hữu An",
        ["invoiceBuyerType"] = "Organization", ["invoiceName"] = "Công ty ABC", ["taxCode"] = "0100109106", ["invoiceEmail"] = "Ketoan@ABC.vn",
    };

    [Fact]
    public async Task Provinces_and_wards_are_read_by_staff_only()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var provinces = await (await owner.GetAsync("/api/administrative-units/provinces")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["code", "divisionType", "name"], provinces[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Thành phố Hà Nội", "Thành phố Hồ Chí Minh"], provinces.EnumerateArray().Select(p => p.GetProperty("name").GetString()));

        var wards = await (await owner.GetAsync("/api/administrative-units/provinces/1/wards")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([4, 10], wards.EnumerateArray().Select(w => w.GetProperty("code").GetInt32()));
        Assert.All(wards.EnumerateArray(), w => Assert.Equal(1, w.GetProperty("provinceCode").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/administrative-units/provinces/999/wards")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/administrative-units/provinces")).StatusCode);
        var trader = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync("/api/administrative-units/provinces")).StatusCode);
    }

    [Fact]
    public async Task A_tax_code_gives_the_company_with_its_address_split()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var found = await (await owner.GetAsync("/api/tax-lookup/0100109106")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("0100109106", "TẬP ĐOÀN CÔNG NGHIỆP - VIỄN THÔNG QUÂN ĐỘI", "NNT đang hoạt động", "Số 5, Phố Nguyễn Hữu An"),
            (found.GetProperty("taxCode").GetString(), found.GetProperty("name").GetString(), found.GetProperty("status").GetString(), found.GetProperty("street").GetString()));
        Assert.Equal((1, "Thành phố Hà Nội"), (found.GetProperty("province").GetProperty("code").GetInt32(), found.GetProperty("province").GetProperty("name").GetString()));
        Assert.Equal((10, "Phường Cầu Giấy"), (found.GetProperty("ward").GetProperty("code").GetInt32(), found.GetProperty("ward").GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("12345", HttpStatusCode.BadRequest)]
    [InlineData("0100109106-01", HttpStatusCode.BadRequest)]
    [InlineData("0000000001", HttpStatusCode.NotFound)]
    [InlineData("0000000002", HttpStatusCode.ServiceUnavailable)]
    public async Task A_malformed_unknown_or_unreachable_tax_code_is_refused_with_its_status(string taxCode, HttpStatusCode status)
    {
        var owner = await factory.PurchasingOwnerAsync();

        var response = await owner.GetAsync($"/api/tax-lookup/{taxCode}");

        Assert.Equal(status, response.StatusCode);
        if (status == HttpStatusCode.BadRequest)
        {
            Assert.Equal("Tax code", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }
    }

    [Fact]
    public async Task The_tax_code_lookup_is_for_staff_who_may_create_or_edit_customers_or_suppliers()
    {
        var trader = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username);

        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync("/api/tax-lookup/0100109106")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/tax-lookup/0100109106")).StatusCode);
    }

    [Fact]
    public async Task Tax_lookups_over_the_per_minute_limit_of_the_account_get_429()
    {
        await factory.EnsureCooperativeAsync();
        var ownerAccount = await factory.AddAccountAsync(NewCode(), Password);
        var otherAccount = await factory.AddAccountAsync(NewCode(), Password);
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:TaxLookupsPerMinute", "2"));
        async Task<HttpClient> SignedInAsync(string username) =>
            WithBearer(api.CreateClient(), (await LoginTokensAsync(api.CreateClient(), username, Password)).GetProperty("accessToken").GetString()!);
        var owner = await SignedInAsync(ownerAccount.Username);
        var other = await SignedInAsync(otherAccount.Username);

        var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => owner.GetAsync("/api/tax-lookup/0100109106")));

        Assert.Equal(2, answers.Count(r => r.StatusCode == HttpStatusCode.OK));
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
        // The limit is the account's: another account still asks.
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/tax-lookup/0100109106")).StatusCode);
    }

    [Fact]
    public async Task An_organization_customer_is_saved_with_its_invoice_info_and_the_names_of_the_list()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var body = Organization();
        body["provinceName"] = "ha noi";
        body["wardName"] = "cau giay";

        var saved = (await (await owner.PostAsJsonAsync("/api/customers", body)).ReadAsync(HttpStatusCode.Created)).GetProperty("customer");

        Assert.Equal(("Thành phố Hà Nội", "Phường Cầu Giấy", "Số 5, Phố Nguyễn Hữu An"), (saved.GetProperty("province").GetString(), saved.GetProperty("ward").GetString(), saved.GetProperty("address").GetString()));
        Assert.Equal((1, 10, "Organization", "Công ty ABC", "0100109106"), (saved.GetProperty("provinceCode").GetInt32(), saved.GetProperty("wardCode").GetInt32(),
            saved.GetProperty("invoiceBuyerType").GetString(), saved.GetProperty("invoiceName").GetString(), saved.GetProperty("taxCode").GetString()));
        Assert.Equal(("ketoan@abc.vn", true, false), (saved.GetProperty("invoiceEmail").GetString(), saved.GetProperty("invoiceAddressSameAsContact").GetBoolean(), saved.GetProperty("addressNeedsReview").GetBoolean()));
        Assert.False(saved.TryGetProperty("identityNumber", out _));
    }

    [Fact]
    public async Task An_organization_customer_without_the_email_a_picked_address_or_a_ward_of_its_province_is_refused()
    {
        var owner = await factory.PurchasingOwnerAsync();

        async Task<string?> FieldOf(Action<Dictionary<string, object?>> change)
        {
            var body = Organization();
            change(body);
            return await (await owner.PostAsJsonAsync("/api/customers", body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        }

        Assert.Equal("Invoice email", await FieldOf(b => b["invoiceEmail"] = null));
        Assert.Equal("Tax code", await FieldOf(b => b["taxCode"] = "12345"));
        Assert.Equal("Invoice name", await FieldOf(b => b["invoiceName"] = " "));
        Assert.Equal("Ward", await FieldOf(b => { b["wardCode"] = 300; b["wardName"] = "Phường Bến Nghé"; }));
        Assert.Equal("Province", await FieldOf(b => { b["provinceCode"] = 999; }));
        Assert.Equal("Province", await FieldOf(b => { b["provinceCode"] = null; b["provinceName"] = null; b["wardCode"] = null; b["wardName"] = null; }));
        Assert.Equal("Invoice ward", await FieldOf(b => { b["invoiceAddressSameAsContact"] = false; b["invoiceProvinceCode"] = 1; b["invoiceProvinceName"] = "Hà Nội"; b["invoiceStreet"] = "1 Hàng Bài"; }));
    }

    [Fact]
    public async Task A_consumer_customer_has_no_invoice_info_and_an_individual_needs_only_a_name_and_an_address()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var consumer = (await (await owner.PostAsJsonAsync("/api/customers", new { name = $"Khách {NewCode()}", phoneNumber = NewPhone(), invoiceName = "Bỏ qua", taxCode = "0100109106" })).ReadAsync(HttpStatusCode.Created)).GetProperty("customer");
        Assert.Equal("Consumer", consumer.GetProperty("invoiceBuyerType").GetString());
        Assert.Equal(JsonValueKind.Null, consumer.GetProperty("invoiceName").ValueKind);
        Assert.Equal(JsonValueKind.Null, consumer.GetProperty("taxCode").ValueKind);

        var individual = Organization();
        individual["invoiceBuyerType"] = "Individual";
        individual["invoiceName"] = "Nguyễn Văn An";
        individual["invoiceEmail"] = null;
        individual["taxCode"] = "0100109106";
        individual["personalTaxId"] = "012345678901";
        var saved = (await (await owner.PostAsJsonAsync("/api/customers", individual)).ReadAsync(HttpStatusCode.Created)).GetProperty("customer");
        Assert.Equal(("Individual", "012345678901"), (saved.GetProperty("invoiceBuyerType").GetString(), saved.GetProperty("personalTaxId").GetString()));
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("taxCode").ValueKind);
    }

    [Fact]
    public async Task A_supplier_may_not_be_a_consumer_and_an_organization_supplier_is_saved_with_its_invoice_info()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var body = Organization();
        body["supplierType"] = "Seed";

        var consumer = new Dictionary<string, object?>(body) { ["invoiceBuyerType"] = "Consumer" };
        Assert.Equal("Invoice buyer type", await (await owner.PostAsJsonAsync("/api/suppliers", consumer)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var saved = await (await owner.PostAsJsonAsync("/api/suppliers", body)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(("Organization", "Công ty ABC", "0100109106", "ketoan@abc.vn"),
            (saved.GetProperty("invoiceBuyerType").GetString(), saved.GetProperty("invoiceName").GetString(), saved.GetProperty("taxCode").GetString(), saved.GetProperty("invoiceEmail").GetString()));
        Assert.Equal((1, 10, false), (saved.GetProperty("provinceCode").GetInt32(), saved.GetProperty("wardCode").GetInt32(), saved.GetProperty("addressNeedsReview").GetBoolean()));
    }
}
