using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-04.3 on a real SQL Server: merges that race answer 2xx or 409, never 500, and leave no chain.</summary>
public class CustomerMergeSqlServerTests
{
    [SqlServerFact]
    public async Task Parallel_merges_of_one_profile_merge_once_and_never_answer_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var kept = await owner.CreateCustomerAsync();
        var otherKept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync();

        var responses = await InParallelAsync(6, i => owner.PostMergeAsync(
            i % 2 == 0 ? kept.Id() : otherKept.Id(), merged.Id(), i % 2 == 0 ? kept.RowVersion() : otherKept.RowVersion(), merged.RowVersion()));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertAnswersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Customer.Merged" && e.RecordId == merged.Id())));
    }

    // Opposite merges at once must not leave two profiles pointing at each other.
    [SqlServerFact]
    public async Task Opposite_parallel_merges_leave_one_kept_profile_and_never_answer_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < 5; round++)
        {
            var first = await owner.CreateCustomerAsync();
            var second = await owner.CreateCustomerAsync();

            var responses = await InParallelAsync(2, i => i == 0
                ? owner.PostMergeAsync(first.Id(), second.Id(), first.RowVersion(), second.RowVersion())
                : owner.PostMergeAsync(second.Id(), first.Id(), second.RowVersion(), first.RowVersion()));

            await AssertAnswersAsync(responses, "200", "409 MSG06", "409 MSG-GEN-09");
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            var mergedCount = await host.QueryAsync(db => db.Customers.CountAsync(c => (c.Id == first.Id() || c.Id == second.Id()) && c.MergedIntoCustomerId != null));
            Assert.Equal(1, mergedCount);
        }
    }

    // A chain merged while the middle profile moves: no profile may stay on a merged one.
    [SqlServerFact]
    public async Task A_merge_into_a_profile_that_is_being_merged_keeps_no_chain()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < 5; round++)
        {
            var top = await owner.CreateCustomerAsync();
            var middle = await owner.CreateCustomerAsync();
            var bottom = await owner.CreateCustomerAsync();

            var responses = await InParallelAsync(2, i => i == 0
                ? owner.PostMergeAsync(top.Id(), middle.Id(), top.RowVersion(), middle.RowVersion())
                : owner.PostMergeAsync(middle.Id(), bottom.Id(), middle.RowVersion(), bottom.RowVersion()));

            await AssertAnswersAsync(responses, "200", "409 MSG06", "409 MSG-GEN-09");
            var rows = await host.QueryAsync(db => db.Customers.AsNoTracking().Where(c => c.Id == top.Id() || c.Id == middle.Id() || c.Id == bottom.Id()).ToListAsync());
            var mergedIds = rows.Where(r => r.MergedIntoCustomerId is not null).Select(r => r.Id).ToHashSet();
            Assert.DoesNotContain(rows, r => r.MergedIntoCustomerId is { } target && mergedIds.Contains(target));
        }
    }

    [SqlServerFact]
    public async Task A_merge_and_an_edit_of_the_merged_profile_at_once_answer_2xx_or_409()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync();

        var responses = await InParallelAsync(2, i => i == 0
            ? owner.PostMergeAsync(kept.Id(), merged.Id(), kept.RowVersion(), merged.RowVersion())
            : owner.PutAsJsonAsync($"/api/customers/{merged.Id()}", new { name = "Edited", phoneNumber = merged.Text("phoneNumber"), rowVersion = merged.RowVersion() }));

        await AssertAnswersAsync(responses, "200", "409 MSG06", "409 MSG-GEN-09");
    }

    // The filtered unique index leaves merged rows out, so the number is free for a new staff save.
    [SqlServerFact]
    public async Task A_merged_profile_keeps_its_row_in_the_database_with_the_number_released()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var phone = NewPhone();
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync(phone: phone);
        await (await owner.MergeAsync(kept.Id(), merged.Id())).ReadAsync(HttpStatusCode.OK);

        var again = await owner.CreateCustomerAsync(phone: phone);

        Assert.Equal(2, await host.QueryAsync(db => db.Customers.CountAsync(c => c.PhoneNumber == phone)));
        Assert.NotEqual(merged.Id(), again.Id());
    }
}
