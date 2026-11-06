using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Infrastructure.Messaging.Fakes;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: merges that race on a real SQL Server, against each other, edits and registration.</summary>
public class SalesCatalogueQaMergeRaceTests
{
    private const int Rounds = 8;
    private static readonly string[] Allowed = ["200", "409 MSG06", "409 MSG-GEN-09", "409 MSG-GEN-02"];

    private static async Task AssertConsistentAsync(FarmingHost host)
    {
        var rows = await host.QueryAsync(db => db.Customers.AsNoTracking().Select(c => new { c.Id, c.MergedIntoCustomerId, c.AccountId }).ToListAsync());
        var merged = rows.Where(r => r.MergedIntoCustomerId is not null).Select(r => r.Id).ToHashSet();
        Assert.DoesNotContain(rows, r => r.MergedIntoCustomerId is { } target && merged.Contains(target));
        Assert.DoesNotContain(rows, r => r.MergedIntoCustomerId is not null && r.AccountId is not null);
        var audited = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "Customer.Merged").Select(e => e.RecordId).ToListAsync());
        Assert.Equal(merged.Count, audited.Count);
    }

    // A into B, B into C and C into A at once: no cycle, no chain, one audit row per merged profile.
    [SqlServerFact]
    public async Task A_ring_of_merges_at_once_leaves_no_cycle_and_no_chain()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < Rounds; round++)
        {
            var (a, b, c) = (await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync());

            var responses = await InParallelAsync(3, i => i switch
            {
                0 => owner.PostMergeAsync(b.Id(), a.Id(), b.RowVersion(), a.RowVersion()),
                1 => owner.PostMergeAsync(c.Id(), b.Id(), c.RowVersion(), b.RowVersion()),
                _ => owner.PostMergeAsync(a.Id(), c.Id(), a.RowVersion(), c.RowVersion()),
            });

            await AssertAnswersAsync(responses, Allowed);
            await AssertConsistentAsync(host);
        }
    }

    // Two merges into one kept profile: both may not win on a stale kept version, and nothing is half done.
    [SqlServerFact]
    public async Task Merges_into_one_kept_profile_and_an_edit_of_it_at_once_answer_2xx_or_409()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < Rounds; round++)
        {
            var (kept, first, second) = (await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync(), await owner.CreateCustomerAsync());

            var responses = await InParallelAsync(3, i => i switch
            {
                0 => owner.PostMergeAsync(kept.Id(), first.Id(), kept.RowVersion(), first.RowVersion()),
                1 => owner.PostMergeAsync(kept.Id(), second.Id(), kept.RowVersion(), second.RowVersion()),
                _ => owner.PutAsJsonAsync($"/api/customers/{kept.Id()}", new { name = "Edited", phoneNumber = kept.Text("phoneNumber"), rowVersion = kept.RowVersion() }),
            });

            await AssertAnswersAsync(responses, Allowed);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            await AssertConsistentAsync(host);
        }
    }

    // BR-SALE-04: the merged profile must have no account. A registration that links it at the same moment must conflict.
    [SqlServerFact]
    public async Task A_merge_and_a_registration_linking_the_merged_profile_at_once_never_leave_a_merged_profile_with_an_account()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < Rounds; round++)
        {
            var phone = NewPhone();
            var kept = await owner.CreateCustomerAsync();
            var merged = await owner.CreateCustomerAsync(phone: phone);
            var anonymous = host.Api.CreateClient();
            var started = await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = phone });
            var code = Regex.Match(host.Api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone)[0].Text, @"\d{6}").Value;
            var token = await VerifyAsync(anonymous, await VerificationIdAsync(started), code);
            var password = NewPassword();

            var responses = await InParallelAsync(2, i => i == 0
                ? owner.PostMergeAsync(kept.Id(), merged.Id(), kept.RowVersion(), merged.RowVersion())
                : anonymous.PostAsJsonAsync("/api/auth/register", new { channel = "Sms", identifier = phone, password, passwordConfirmation = password, verificationToken = token }));

            await AssertAnswersAsync([responses[0]], Allowed);
            Assert.True((int)responses[1].StatusCode < 500, await responses[1].Content.ReadAsStringAsync());
            await AssertConsistentAsync(host);

            // A trader who lost the race is not stuck: the same verification still registers, with a new profile if the merge won.
            if (responses[1].StatusCode != HttpStatusCode.Created)
            {
                var retry = await anonymous.PostAsJsonAsync("/api/auth/register", new { channel = "Sms", identifier = phone, password, passwordConfirmation = password, verificationToken = token });
                await retry.ReadAsync(HttpStatusCode.Created);
            }
        }
    }

    // Merge against a new profile that takes the number being released: the unique index and the pre-check decide.
    [SqlServerFact]
    public async Task A_merge_and_a_new_profile_with_the_released_number_at_once_answer_2xx_or_409()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < Rounds; round++)
        {
            var phone = NewPhone();
            var kept = await owner.CreateCustomerAsync();
            var merged = await owner.CreateCustomerAsync(phone: phone);

            var responses = await InParallelAsync(2, i => i == 0
                ? owner.PostMergeAsync(kept.Id(), merged.Id(), kept.RowVersion(), merged.RowVersion())
                : owner.PostAsJsonAsync("/api/customers", new { name = "New", phoneNumber = phone }));

            await AssertAnswersAsync([responses[0]], Allowed);
            await AssertAnswersAsync([responses[1]], "201", "409 MSG-GEN-02");
            await AssertConsistentAsync(host);
            // None when the new profile lost to the old row before the merge committed; never two.
            var live = await host.QueryAsync(db => db.Customers.CountAsync(c => c.PhoneNumber == phone && c.MergedIntoCustomerId == null));
            Assert.InRange(live, 0, 1);
        }
    }

    // The version that comes back from a merge opens the next edit; the one before it no longer does, on either side.
    [SqlServerFact]
    public async Task A_merge_moves_both_versions_and_answers_the_new_one_of_the_kept_profile()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync();

        var after = await (await owner.PostMergeAsync(kept.Id(), merged.Id(), kept.RowVersion(), merged.RowVersion())).ReadAsync(HttpStatusCode.OK);

        Assert.NotEqual(kept.RowVersion(), after.RowVersion());
        Assert.NotEqual(merged.RowVersion(), (await owner.CustomerAsync(merged.Id())).RowVersion());
        await (await owner.PutAsJsonAsync($"/api/customers/{kept.Id()}", new { name = "X", phoneNumber = kept.Text("phoneNumber"), rowVersion = kept.RowVersion() })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        await (await owner.PutAsJsonAsync($"/api/customers/{kept.Id()}", new { name = "X", phoneNumber = kept.Text("phoneNumber"), rowVersion = after.RowVersion() })).ReadAsync(HttpStatusCode.OK);
    }

    // A form opened on a preview: an edit between the preview and the merge is a stale version on that side.
    [SqlServerFact]
    public async Task An_edit_after_the_preview_makes_the_merge_stale_on_each_side()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync();
        var preview = await (await owner.GetAsync($"/api/customers/{kept.Id()}/merge-preview?mergedCustomerId={merged.Id()}")).ReadAsync(HttpStatusCode.OK);
        var (keptVersion, mergedVersion) = (preview.GetProperty("kept").RowVersion(), preview.GetProperty("merged").RowVersion());

        await (await owner.PutAsJsonAsync($"/api/customers/{merged.Id()}", new { name = "Edited", phoneNumber = merged.Text("phoneNumber"), rowVersion = mergedVersion })).ReadAsync(HttpStatusCode.OK);
        await (await owner.PostMergeAsync(kept.Id(), merged.Id(), keptVersion, mergedVersion)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        await (await owner.PutAsJsonAsync($"/api/customers/{kept.Id()}", new { name = "Edited", phoneNumber = kept.Text("phoneNumber"), rowVersion = keptVersion })).ReadAsync(HttpStatusCode.OK);
        var freshMerged = await owner.CustomerAsync(merged.Id());
        await (await owner.PostMergeAsync(kept.Id(), merged.Id(), keptVersion, freshMerged.RowVersion())).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal(JsonValueKind.Null, (await owner.CustomerAsync(merged.Id())).GetProperty("mergedIntoCustomerId").ValueKind);
    }
}
