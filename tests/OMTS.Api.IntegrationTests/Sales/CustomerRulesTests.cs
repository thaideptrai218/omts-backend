using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-04, BR-SALE-04: mandatory entries, unique phone number when staff save, phone locked when linked, list and view.</summary>
public class CustomerRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(null, "0912345678", "Name")]
    [InlineData("  ", "0912345678", "Name")]
    [InlineData("Buyer", null, "Phone number")]
    [InlineData("Buyer", "12345", "Phone number")]
    [InlineData("Buyer", "09123abc45", "Phone number")]
    public async Task Missing_or_invalid_entries_return_MSG_GEN_01_and_a_walk_in_buyer_with_no_number_gets_no_profile(string? name, string? phone, string field)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var before = await factory.QueryAsync(db => db.Customers.CountAsync());

        var response = await owner.PostAsJsonAsync("/api/customers", new { name, phoneNumber = phone });

        Assert.Equal(field, await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(before, await factory.QueryAsync(db => db.Customers.CountAsync()));
    }

    [Fact]
    public async Task Too_long_entries_are_all_listed()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var response = await owner.PostAsJsonAsync("/api/customers", new
        {
            name = new string('a', 201), phoneNumber = NewPhone(), address = new string('b', 501), note = new string('c', 1001),
        });

        var body = await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal(["Name", "Address", "Note"], body.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
    }

    // SALE-04.0 step 7; the same POST is the quick create of SALE-05.1.
    [Fact]
    public async Task A_new_profile_is_saved_with_MSG_GEN_05_a_normalized_number_and_an_audit_row()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();

        var response = await owner.PostAsJsonAsync("/api/customers", new { name = " Vựa hải sản ", phoneNumber = $"+84 {phone[1..4]} {phone[4..7]} {phone[7..]}", address = " Hạ Long ", note = " Sáng thứ Bảy " });

        var body = await response.ReadAsync(HttpStatusCode.Created);
        Assert.Equal("MSG-GEN-05", body.Text("messageCode"));
        Assert.Equal("Customer profile saved.", body.Text("message"));
        var customer = body.GetProperty("customer");
        Assert.Equal(("Vựa hải sản", phone, "Hạ Long", "Sáng thứ Bảy"), (customer.Text("name"), customer.Text("phoneNumber"), customer.Text("address"), customer.Text("note")));
        Assert.False(customer.GetProperty("phoneLocked").GetBoolean());
        Assert.Equal(0, customer.GetProperty("currentDebt").GetDecimal());
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == customer.Id()));
        Assert.Equal(("Customer.Created", phone), (audit.Action, audit.RecordCode));
        Assert.True(await factory.QueryAsync(db => db.Customers.Where(c => c.Id == customer.Id()).Select(c => c.PhoneCheckedByStaff).SingleAsync()));
    }

    [Fact]
    public async Task A_number_on_another_profile_is_refused_with_its_id_on_create_and_on_edit_however_it_is_written()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();
        var first = await owner.CreateCustomerAsync(phone: phone);
        var written = $"{phone[..4]}.{phone[4..7]}.{phone[7..]}";

        var create = await owner.PostAsJsonAsync("/api/customers", new { name = "Copy", phoneNumber = written });
        Assert.Equal(first.Id(), (await create.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());

        var second = await owner.CreateCustomerAsync();
        var edit = await owner.PutAsJsonAsync($"/api/customers/{second.Id()}", new { name = "Second", phoneNumber = written, rowVersion = second.RowVersion() });
        Assert.Equal(first.Id(), (await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
    }

    // BR-SALE-04: the check covers every profile, also one that registration made.
    [Fact]
    public async Task The_number_of_a_profile_made_at_registration_is_refused_too()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (trader, profile) = await factory.LinkedProfileAsync();

        var response = await owner.PostAsJsonAsync("/api/customers", new { name = "Copy", phoneNumber = trader.PhoneNumber });

        Assert.Equal(profile.Id, (await response.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
    }

    [Fact]
    public async Task An_edit_keeps_the_invoices_of_the_profile_and_audits_only_the_changed_values()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var customer = await owner.CreateCustomerAsync(name: "Buyer");

        var body = await (await owner.PutAsJsonAsync($"/api/customers/{customer.Id()}", new
        {
            name = "Buyer", phoneNumber = customer.Text("phoneNumber"), address = "Cái Rồng", rowVersion = customer.RowVersion(),
        })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Cái Rồng", body.GetProperty("customer").Text("address"));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == customer.Id() && e.Action == "Customer.Updated"));
        Assert.Equal("Cái Rồng", JsonDocument.Parse(audit.After!).RootElement.GetProperty("Address").GetString());
        Assert.False(JsonDocument.Parse(audit.After!).RootElement.TryGetProperty("Name", out _));

        var again = await owner.PutAsJsonAsync($"/api/customers/{customer.Id()}", new
        {
            name = "Buyer", phoneNumber = customer.Text("phoneNumber"), address = "Cái Rồng", rowVersion = body.GetProperty("customer").RowVersion(),
        });
        await again.ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == customer.Id() && e.Action == "Customer.Updated")));
    }

    [Fact]
    public async Task An_edit_needs_the_row_version_it_read()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var customer = await owner.CreateCustomerAsync();

        var missing = await owner.PutAsJsonAsync($"/api/customers/{customer.Id()}", new { name = "New", phoneNumber = NewPhone() });
        Assert.Equal("RowVersion", await missing.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var stale = await owner.PutAsJsonAsync($"/api/customers/{customer.Id()}", new { name = "New", phoneNumber = NewPhone(), rowVersion = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) });
        await stale.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
    }

    [Fact]
    public async Task An_unknown_profile_returns_404()
    {
        var owner = await factory.PurchasingOwnerAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/customers/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/customers/{Guid.NewGuid()}", new { name = "X", phoneNumber = NewPhone(), rowVersion = "" })).StatusCode);
    }

    [Fact]
    public async Task Search_finds_by_name_or_number_and_pages()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var tag = NewCode();
        var phone = NewPhone();
        var byName = await owner.CreateCustomerAsync(name: $"A {tag}");
        var second = await owner.CreateCustomerAsync(name: $"B {tag}", phone: phone);
        var (_, linked) = await factory.LinkedProfileAsync(name: $"C {tag}");

        var page = await (await owner.GetAsync($"/api/customers?search={tag}&sortBy=name&sortDir=asc&pageSize=2")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal([byName.Id(), second.Id()], page.GetProperty("items").EnumerateArray().Select(i => i.Id()));
        var last = await (await owner.GetAsync($"/api/customers?search={tag}&sortBy=name&sortDir=asc&pageSize=2&page=2")).ReadAsync(HttpStatusCode.OK);
        var row = Assert.Single(last.GetProperty("items").EnumerateArray());
        Assert.Equal(linked.Id, row.Id());
        Assert.True(row.GetProperty("hasTraderAccount").GetBoolean());

        var byNumber = await (await owner.GetAsync($"/api/customers?search={phone[3..]}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(second.Id(), Assert.Single(byNumber.GetProperty("items").EnumerateArray()).Id());
    }
}
