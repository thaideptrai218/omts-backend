using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Sales;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-11 and SALE-04 on a real SQL Server: unique indexes and parallel saves answering 2xx or 409, never 500.</summary>
public class SalesCatalogueSqlServerTests
{
    [SqlServerFact]
    public async Task Parallel_products_with_one_name_create_one_and_answer_MSG_GEN_02_with_its_id()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var name = $"Hàu {NewCode()}";

        var responses = await InParallelAsync(6, i => owner.PostAsJsonAsync("/api/products", new { name = i % 2 == 0 ? name : name.ToUpperInvariant(), unitPrice = 60_000 }));

        var created = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var id = (await created.ReadAsync(HttpStatusCode.Created)).GetProperty("product").Id();
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(id, (await loser.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
        }

        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Product.Created" && e.RecordId == id)));
    }

    [SqlServerFact]
    public async Task Parallel_edits_to_one_name_and_parallel_status_changes_never_answer_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var products = new[] { await owner.CreateProductAsync(), await owner.CreateProductAsync(), await owner.CreateProductAsync() };
        var name = $"Hàu {NewCode()}";

        var edits = await InParallelAsync(3, i => owner.PutAsJsonAsync($"/api/products/{products[i].Id()}", new { name, unitPrice = 70_000, rowVersion = products[i].RowVersion() }));
        Assert.Single(edits, r => r.StatusCode == HttpStatusCode.OK);
        await AssertAnswersAsync(edits.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG-GEN-02");

        var changes = await InParallelAsync(6, i => owner.PostAsync($"/api/products/{products[0].Id()}/discontinue", null));
        Assert.Single(changes, r => r.StatusCode == HttpStatusCode.OK);
        await AssertAnswersAsync(changes.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Product.Discontinued" && e.RecordId == products[0].Id())));
    }

    [SqlServerFact]
    public async Task A_price_of_zero_or_a_repeated_name_is_refused_by_the_database_too()
    {
        await using var host = await FarmingHost.StartAsync();
        var product = await host.Owner.Client.CreateProductAsync(unitPrice: 60_000);

        await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.Products.Add(new Product { Code = Guid.NewGuid().ToString("N")[..12], Name = NewCode(), UnitPrice = 0 })));
        await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.Products.Add(new Product { Code = Guid.NewGuid().ToString("N")[..12], Name = product.Text("name")!, UnitPrice = 1 })));
    }

    // BR-SALE-04 and the design question Q17: the check is strict for staff saves, also when they race.
    [SqlServerFact]
    public async Task Parallel_profiles_with_one_number_create_one_and_answer_MSG_GEN_02_with_its_id()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var phone = NewPhone();
        var spellings = new[] { phone, $"+84{phone[1..]}", $"{phone[..4]} {phone[4..7]} {phone[7..]}" };

        var responses = await InParallelAsync(6, i => owner.PostAsJsonAsync("/api/customers", new { name = $"Buyer {i}", phoneNumber = spellings[i % 3] }));

        var created = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var id = (await created.ReadAsync(HttpStatusCode.Created)).GetProperty("customer").Id();
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(id, (await loser.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
        }

        Assert.Equal(1, await host.QueryAsync(db => db.Customers.CountAsync(c => c.PhoneNumber == phone)));
    }

    [SqlServerFact]
    public async Task Parallel_edits_to_one_number_save_one()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var customers = new[] { await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync() };
        var phone = NewPhone();

        var responses = await InParallelAsync(4, i => owner.PutAsJsonAsync($"/api/customers/{customers[i].Id()}", new { name = "Buyer", phoneNumber = phone, rowVersion = customers[i].RowVersion() }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertAnswersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG-GEN-02");
        Assert.Equal(1, await host.QueryAsync(db => db.Customers.CountAsync(c => c.PhoneNumber == phone)));
    }

    // BR-SALE-04, BR-ACCOUNT-04: registration and a trader changing a number are not checked; the index covers staff saves only.
    [SqlServerFact]
    public async Task Registration_and_a_traders_new_number_may_repeat_a_number_and_free_the_old_one()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var phone = NewPhone();
        var offline = await owner.CreateCustomerAsync(phone: phone);

        // Registration made a second profile with this number (the database takes it).
        var registered = new Customer { Code = OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit.NewCustomerCode(), CooperativeId = host.CooperativeId, Name = "Registered", PhoneNumber = phone };
        await host.UseDbAsync(db => db.Customers.Add(registered));
        Assert.Equal(2, await host.QueryAsync(db => db.Customers.CountAsync(c => c.PhoneNumber == phone)));

        // A trader on a profile staff saved changes the number to one that another profile holds.
        var saved = await owner.CreateCustomerAsync();
        var oldNumber = saved.Text("phoneNumber")!;
        var row = await host.QueryAsync(db => db.Customers.SingleAsync(c => c.Id == saved.Id()));
        await host.UseDbAsync(db =>
        {
            db.Attach(row);
            row.PhoneNumber = phone;
        });
        Assert.Equal(3, await host.QueryAsync(db => db.Customers.CountAsync(c => c.PhoneNumber == phone)));
        Assert.False(await host.QueryAsync(db => db.Customers.Where(c => c.Id == saved.Id()).Select(c => c.PhoneCheckedByStaff).SingleAsync()));

        // The old number is free for a staff save; the repeated one is not, whichever profile the check finds.
        await owner.CreateCustomerAsync(phone: oldNumber);
        var repeated = await owner.PostAsJsonAsync("/api/customers", new { name = "Copy", phoneNumber = phone });
        await repeated.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.NotEqual(Guid.Empty, offline.Id());
    }
}
