// PENDING – fails on SQL Server because of product bugs found in the OMTS-209 QA review (qa1-261023-omts-209-review.md).
// Rename to .cs once fixed; needs OMTS_TEST_SQLSERVER.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

public class PurchasingSqlServerBugTests
{
    // C1: new lines / seed lots attached to a tracked receipt are saved as UPDATEs of rows that do not exist → 409 MSG06.
    [SqlServerFact]
    public async Task A_draft_with_lines_can_be_edited_and_a_draft_with_seed_lines_completed()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));

        var edited = await (await world.Owner.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body(new LineBody(world.Seed, 10, 20_000)) with
        {
            RowVersion = draft.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        var completed = await (await world.Owner.PostAsJsonAsync($"/api/purchase-receipts/{draft.Id()}/complete", new { rowVersion = edited.GetProperty("rowVersion").GetString() }))
            .ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.Equal(1, await host.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.PurchaseReceiptLine.ReceiptId == draft.Id())));
    }

    // C1 + INP-03 "A double Complete never creates seed lots or the payment twice", for a draft that has seed lines.
    [SqlServerFact]
    public async Task Parallel_completes_of_a_draft_with_seed_lines_create_its_seed_lots_and_payment_once()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 10, 100_000), new LineBody(world.Seed, 5, 100_000)) with
        {
            AmountPaidNow = 300_000, PaidNowMethod = "Cash",
        });

        var responses = await InParallelAsync(6, _ => world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(2, await host.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.PurchaseReceiptLine.ReceiptId == draft.Id())));
        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.ReceiptId == draft.Id())));
    }

    // H2: a draft with a discount or paid-now amount that is not a multiple of 1,000 breaks the CK_*_Thousands check → 500.
    // H3: a draft with an unknown raft id breaks the raft foreign key → 500.
    [SqlServerFact]
    public async Task Bad_draft_amounts_or_an_unknown_raft_are_refused_without_a_server_error()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();

        var discount = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)) with { DiscountAmount = 1500 });
        Assert.Equal("Discount amount", await discount.FieldAsync(HttpStatusCode.BadRequest, "MSG41"));
        var paidNow = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)) with { AmountPaidNow = 2500, PaidNowMethod = "Cash" });
        Assert.Equal("Amount paid now", await paidNow.FieldAsync(HttpStatusCode.BadRequest, "MSG41"));
        var raft = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Material, 1, 10_000)) with { RaftId = Guid.NewGuid() });
        Assert.Equal("Raft", await raft.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    // H1: Supplier.Name allows 200 characters, but every Supplier.* audit row stores it in AuditEvent.RecordCode (100) → 500.
    [SqlServerFact]
    public async Task A_supplier_name_of_200_characters_is_saved_and_audited()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var created = await (await owner.PostAsJsonAsync("/api/suppliers", new { name = new string('Ư', 200), supplierType = "Seed", phoneNumber = NewPhone() }))
            .ReadAsync(HttpStatusCode.Created);
        await (await owner.PostAsync($"/api/suppliers/{created.Id()}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == created.Id())));
    }

    // M: the copy takes the next daily code without the retry that create uses, so parallel copies (or a copy beside a create) collide → 409 MSG03.
    [SqlServerFact]
    public async Task Parallel_copies_get_distinct_codes()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var source = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 10_000));

        var copies = await InParallelAsync(4, _ => world.Owner.PostAsync($"/api/purchase-receipts/{source.Id()}/copy", null));

        foreach (var copy in copies)
        {
            await copy.ReadAsync(HttpStatusCode.Created);
        }

        Assert.Equal(5, (await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.SupplierId == world.SupplierId).Select(r => r.Code).ToListAsync())).Distinct().Count());
    }

    // L: a parallel duplicate (unique index) answers a generic "Record already exists" MSG03 without the existing id the ticket asks for.
    [SqlServerFact]
    public async Task A_parallel_duplicate_phone_answers_MSG03_with_the_existing_supplier_id()
    {
        await using var host = await FarmingHost.StartAsync();
        var phone = NewPhone();
        var responses = await InParallelAsync(6, i => host.Owner.Client.PostAsJsonAsync("/api/suppliers", new { name = $"S{i}", supplierType = "Seed", phoneNumber = phone }));
        var id = await host.QueryAsync(db => db.Suppliers.Where(s => s.PhoneNumber == phone).Select(s => s.Id).SingleAsync());
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(id, (await refused.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
        }
    }

    // L: "A repeated DeduplicationKey returns the first payment and creates nothing" – parallel retries get 409 instead.
    [SqlServerFact]
    public async Task Parallel_requests_with_one_deduplication_key_all_return_the_first_payment()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 10, 100_000));
        var key = $"retry-{Guid.NewGuid():N}";
        var responses = await InParallelAsync(5, _ => world.Owner.PostPaymentAsync(new PaymentBody
        {
            SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 100_000, DeduplicationKey = key,
        }));
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await response.ReadAsync(HttpStatusCode.Created)).Id());
        }

        Assert.Single(ids);
    }
}
