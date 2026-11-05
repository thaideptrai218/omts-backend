using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223, SALE-04: phone numbers, linked profiles, registration against staff saves, malformed input.</summary>
public class SalesCatalogueQaCustomerTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Spell(string phone, string format) => format switch
    {
        "+84" => $"+84{phone[1..]}",
        "84" => $"84{phone[1..]}",
        "spaces" => $"{phone[..4]} {phone[4..7]} {phone[7..]}",
        "dots" => $"{phone[..4]}.{phone[4..7]}.{phone[7..]}",
        "dashes" => $"{phone[..4]}-{phone[4..7]}-{phone[7..]}",
        "brackets" => $"({phone[..4]}) {phone[4..7]}-{phone[7..]}",
        "nbsp" => $"{phone[..4]}\u00A0{phone[4..]}",
        "+84 spaced" => $"+84 {phone[1..4]} {phone[4..7]} {phone[7..]}",
        _ => phone,
    };

    [Theory]
    [InlineData("+84")]
    [InlineData("84")]
    [InlineData("spaces")]
    [InlineData("dots")]
    [InlineData("dashes")]
    [InlineData("brackets")]
    [InlineData("nbsp")]
    [InlineData("+84 spaced")]
    public async Task Every_spelling_of_a_number_is_stored_as_0_and_counts_as_the_same_number(string format)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();

        var first = await owner.CreateCustomerAsync(phone: Spell(phone, format));
        Assert.Equal(phone, first.Text("phoneNumber"));

        var second = await owner.PostAsJsonAsync("/api/customers", new { name = "Copy", phoneNumber = phone });
        Assert.Equal(first.Id(), (await second.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
    }

    public static TheoryData<string> InvalidNumbers => new()
    {
        "0912", "09123456789", "1912345678", "+1912345678", "+840912345678", "abc", "0912345abc", "++84912345678",
    };

    [Theory]
    [MemberData(nameof(InvalidNumbers))]
    public async Task Numbers_that_are_not_vietnamese_are_msg_gen_01(string phoneNumber)
    {
        var owner = await factory.PurchasingOwnerAsync();

        var response = await owner.PostAsJsonAsync("/api/customers", new { name = "Buyer", phoneNumber });

        Assert.Equal("Phone number", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    // BR-SALE-09 sends SMS to this number, so it never stays in other digits. The account rules (BR-ACCOUNT-02, BR-ACCOUNT-09)
    // read other digits as the same number, so this is not a refusal: stored in ASCII digits, and caught by the uniqueness check.
    [Theory]
    [InlineData('٠')]
    [InlineData('０')]
    public async Task L3_digits_of_other_scripts_are_stored_as_ascii_and_count_as_the_same_number(char zero)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();
        var phoneNumber = "0" + new string(phone[1..].Select(c => (char)(zero + (c - '0'))).ToArray());

        var first = await owner.CreateCustomerAsync(phone: phoneNumber);
        Assert.Equal(phone, first.Text("phoneNumber"));

        var second = await owner.PostAsJsonAsync("/api/customers", new { name = "Copy", phoneNumber = phone });
        Assert.Equal(first.Id(), (await second.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
        var again = await owner.PostAsJsonAsync("/api/customers", new { name = "Copy", phoneNumber });
        Assert.Equal(first.Id(), (await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
    }

    public static TheoryData<string> Malformed => new()
    {
        "", "null", "[]", "123", "{", "{\"name\":123,\"phoneNumber\":\"0912345678\"}", "{\"name\":\"X\",\"phoneNumber\":912345678}", "{\"name\":\"\\ud83d\",\"phoneNumber\":\"0912345678\"}",
        "{\"name\":\"X\",\"phoneNumber\":[\"0912345678\"]}", "{\"name\":\"X\",\"phoneNumber\":\"0912345678\",\"address\":{}}", "{\"name\":\"X\",\"phoneNumber\":\"0912345678\",\"rowVersion\":1}",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Malformed_customer_bodies_are_client_errors(string body)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var customer = await owner.CreateCustomerAsync();

        var post = await owner.PostAsync("/api/customers", Json(body));
        var put = await owner.PutAsync($"/api/customers/{customer.Id()}", Json(body));

        Assert.True((int)post.StatusCode is >= 400 and < 500, $"POST {(int)post.StatusCode}: {await post.Content.ReadAsStringAsync()}");
        Assert.True((int)put.StatusCode is >= 400 and < 500, $"PUT {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData("page=abc")]
    [InlineData("pageSize=abc")]
    [InlineData("page=99999999999")]
    [InlineData("pageSize=99999999999")]
    public async Task Paging_values_that_cannot_be_read_are_client_errors(string query)
    {
        var owner = await factory.PurchasingOwnerAsync();

        var response = await owner.GetAsync($"/api/customers?{query}");

        await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Theory]
    [InlineData("page=0", 1, 20)]
    [InlineData("page=-5", 1, 20)]
    [InlineData("pageSize=0", 1, 1)]
    [InlineData("pageSize=-5", 1, 1)]
    [InlineData("pageSize=1000", 1, 100)]
    public async Task Paging_values_out_of_range_are_brought_into_range(string query, int page, int pageSize)
    {
        var owner = await factory.PurchasingOwnerAsync();

        var body = await (await owner.GetAsync($"/api/customers?{query}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((page, pageSize), (body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32()));
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_with_the_total()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var tag = NewCode();
        await owner.CreateCustomerAsync(name: tag);

        var body = await (await owner.GetAsync($"/api/customers?search={tag}&page=50")).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(1, body.GetProperty("total").GetInt32());
    }

    // SALE-04 step 2 searches by phone number; the same spellings are accepted on save, so the search should read them too.
    [Theory]
    [InlineData("+84")]
    [InlineData("spaces")]
    [InlineData("dots")]
    public async Task M2_a_number_is_found_however_it_is_typed_in_the_search(string format)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();
        var customer = await owner.CreateCustomerAsync(phone: phone);

        var body = await (await owner.GetAsync($"/api/customers?search={Uri.EscapeDataString(Spell(phone, format))}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(customer.Id(), Assert.Single(body.GetProperty("items").EnumerateArray()).Id());
    }
}
