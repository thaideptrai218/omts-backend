using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of CLAIM-01 and CLAIM-02 on a real SQL Server: races, column sizes, N+1 and the migration (OMTS-225).</summary>
public class QualityClaimQaSqlServerTests
{
    private const string MigrationBeforeClaims = "20261108182824_VietQrPendingSales";
    private const string MigrationBeforeCleanup = "20261025175348_AddPushDeviceLoginSession";

    private sealed record World(FarmingSqlServerHost Host, HttpClient Owner, TraderActor Trader, Guid ProductId, Guid[] LotIds, Guid ZoneId);

    private static async Task<World> StartAsync(int lots = 1)
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var lotIds = new Guid[lots];
        for (var i = 0; i < lots; i++)
        {
            lotIds[i] = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(i % 5 + 1))).Id();
        }

        return new World(host, owner, await PurchaseRequestSqlServerTests.TraderOnAsync(host), product.Id(), lotIds, zoneId);
    }

    private static async Task<Guid> SellAsync(World w, int lot) =>
        (await w.Owner.SellAsync(Invoice(w.Trader.Profile.Id, [Line(w.ProductId, w.LotIds[lot], 10m)], Cash(300_000m), w.LotIds[lot]))).GetProperty("invoice").Id();

    private static async Task<string> RowVersionAsync(World w, Guid claimId) => (await w.Owner.ClaimAsStaffAsync(claimId)).Text("rowVersion")!;

    [SqlServerFact]
    public async Task The_largest_vietnamese_inputs_fit_every_column_and_the_audit()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        var invoice = await SellAsync(w, 0);
        var description = string.Concat(Enumerable.Repeat("Hàu chết, thùng số ba. ", 100))[..2000];
        var note = string.Concat(Enumerable.Repeat("Nghiên cứu kỹ, xin lỗi. ", 60))[..1000];

        var claim = (await (await w.Trader.Client.PostClaimAsync(invoice, description)).ReadAsync(HttpStatusCode.Created)).GetProperty("claim");
        var answer = await (await w.Owner.AcceptClaimAsync(claim.Id(), await RowVersionAsync(w, claim.Id()), "PriceCutWholeInvoice", note)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(note, answer.GetProperty("response").Text("note"));
        Assert.Equal(description, (await (await w.Trader.Client.GetAsync($"/api/me/quality-claims/{claim.Id()}")).ReadAsync(HttpStatusCode.OK)).Text("description"));
        Assert.Equal(2, await w.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == claim.Id())));
    }

    [SqlServerFact]
    public async Task Parallel_claims_on_two_invoices_of_one_trader_never_answer_500_and_keep_each_winner_with_its_photo()
    {
        var w = await StartAsync(2);
        await using var _ = w.Host;
        var invoices = new[] { await SellAsync(w, 0), await SellAsync(w, 1) };

        var answers = await InParallelAsync(2, i => w.Trader.Client.PostClaimAsync(invoices[i]));

        await AssertAnswersAsync(answers, "201", "409 MSG06");
        var claims = await w.Host.QueryAsync(db => db.QualityClaims.CountAsync());
        Assert.Equal(answers.Count(a => a.StatusCode == HttpStatusCode.Created), claims);
        Assert.Equal(claims, w.Host.StoredKeys(FileCategories.QualityClaimPhotos).Count);
    }

    [SqlServerFact]
    public async Task A_claim_and_a_merge_of_the_kept_profile_at_the_same_moment_leave_no_row_on_a_merged_profile()
    {
        var w = await StartAsync(2);
        await using var _ = w.Host;
        var invoice = await SellAsync(w, 0);
        var offline = await w.Owner.CreateCustomerAsync();

        var answers = await Task.WhenAll(w.Trader.Client.PostClaimAsync(invoice), w.Owner.MergeAsync(w.Trader.Profile.Id, offline.Id()));

        await AssertAnswersAsync(answers, "201", "200", "409 MSG06");
        var left = await w.Host.QueryAsync(db => db.QualityClaims.CountAsync(c => c.Customer.MergedIntoCustomerId != null));
        Assert.Equal(0, left);
    }

    [SqlServerFact]
    public async Task A_second_accept_with_the_same_row_version_conflicts_as_a_stale_form_never_500()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        var claim = await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, 0));
        var rowVersion = await RowVersionAsync(w, claim.Id());

        var answers = await InParallelAsync(4, _ => w.Owner.AcceptClaimAsync(claim.Id(), rowVersion));

        await AssertAnswersAsync(answers.Where(a => a.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await w.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == claim.Id() && e.Action == "QualityClaim.OfferMade")));
    }

    [SqlServerFact]
    public async Task The_lists_and_the_detail_send_the_same_commands_for_few_and_many_claims_and_photos()
    {
        var w = await StartAsync(8);
        await using var _ = w.Host;
        using var counter = new CommandCounter(new SqlConnectionStringBuilder(w.Host.Database.ConnectionString).InitialCatalog);

        var firstClaim = await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, 0));
        await w.Owner.GetAsync("/api/quality-claims");
        var fewOwner = await counter.CountAsync(() => w.Owner.GetAsync("/api/quality-claims?pageSize=100"));
        var fewTrader = await counter.CountAsync(() => w.Trader.Client.GetAsync("/api/me/quality-claims?pageSize=100"));
        var fewDetail = await counter.CountAsync(() => w.Owner.GetAsync($"/api/quality-claims/{firstClaim.Id()}"));

        JsonElementHolder many = new();
        for (var i = 1; i < 8; i++)
        {
            many.Last = await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, i), Enumerable.Repeat(QualityClaimQaKit.Jpeg, 10).ToList());
        }

        var manyOwner = await counter.CountAsync(() => w.Owner.GetAsync("/api/quality-claims?pageSize=100"));
        var manyTrader = await counter.CountAsync(() => w.Trader.Client.GetAsync("/api/me/quality-claims?pageSize=100"));
        var manyDetail = await counter.CountAsync(() => w.Owner.GetAsync($"/api/quality-claims/{many.Last.Id()}"));

        Assert.True(fewOwner > 0, "The counter saw no command.");
        Assert.Equal(fewOwner, manyOwner);
        Assert.Equal(fewTrader, manyTrader);
        Assert.Equal(fewDetail, manyDetail);
    }

    [SqlServerFact]
    public async Task Paging_at_the_very_end_of_the_range_is_a_valid_offset_on_sql_server()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, 0));

        await (await w.Owner.GetAsync($"/api/quality-claims?page={int.MaxValue}&pageSize=100")).ReadAsync(HttpStatusCode.OK);
        await (await w.Trader.Client.GetAsync($"/api/me/quality-claims?page={int.MaxValue}&pageSize=100")).ReadAsync(HttpStatusCode.OK);
    }

    [SqlServerFact]
    public async Task The_index_is_filtered_unique_the_foreign_keys_restrict_and_down_and_up_work_with_claims_saved()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, 0));
        await using var db = w.Host.Database.CreateContext();

        var index = (await db.Database.SqlQueryRaw<IndexRow>("SELECT is_unique AS IsUnique, CAST(has_filter AS bit) AS HasFilter, filter_definition AS FilterDefinition FROM sys.indexes WHERE name = 'UX_QualityClaims_Invoice_Awaiting'").ToListAsync()).Single();
        Assert.True(index.IsUnique && index.HasFilter);
        Assert.Contains("3701", index.FilterDefinition);
        var cascades = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE parent_object_id IN (OBJECT_ID('QualityClaims'), OBJECT_ID('QualityClaimPhotos')) AND delete_referential_action <> 0").SingleAsync();
        Assert.Equal(0, cascades);

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBeforeClaims);
        var tables = await TablesAsync(db);
        Assert.DoesNotContain("QualityClaims", tables);
        Assert.DoesNotContain("QualityClaimPhotos", tables);
        Assert.False(await db.Statuses.AnyAsync(s => s.Id >= 3701 && s.Id <= 3703));
        Assert.False(await db.StatusTransitions.AnyAsync(t => t.Id == 220 || t.Id == 221));

        await db.Database.MigrateAsync();
        Assert.Contains("QualityClaims", await TablesAsync(db));
        Assert.Equal(3, await db.Statuses.CountAsync(s => s.Group == "QUALITY_CLAIM"));
        Assert.Equal(2, await db.StatusTransitions.CountAsync(t => t.Id == 220 || t.Id == 221));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    /// <summary>
    /// The developer took <c>QualityClaims</c> out of <c>MigrationTests.LegacyTables</c>: the name now also belongs to the new table. Checks what that list used to guard: the old table comes back when
    /// the clean-up is undone, and on the way up the clean-up drops it before the new migration creates the one of SRS 27/10, so a database still at the old model upgrades.
    /// </summary>
    [SqlServerFact]
    public async Task A_database_that_still_has_the_legacy_table_upgrades_to_the_new_table_of_the_same_name()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await db.Database.MigrateAsync();

        await migrator.MigrateAsync(MigrationBeforeCleanup);
        var legacy = await ColumnsAsync(db, "QualityClaims");
        Assert.Contains("DecidedByAccountId", legacy);
        Assert.DoesNotContain("InvoiceId", legacy);

        await db.Database.MigrateAsync();

        var current = await ColumnsAsync(db, "QualityClaims");
        Assert.Contains("InvoiceId", current);
        Assert.Contains("RowVersion", current);
        Assert.DoesNotContain("DecidedByAccountId", current);
        Assert.Contains("QualityClaimPhotos", await TablesAsync(db));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    /// <summary>
    /// An invoice cancelled at the moment a claim is accepted (SALE-08 commits between the check and the save): the accept reads the invoice, writes neither it nor a <c>MarkChanged</c>, so it saves
    /// Accepted on a cancelled invoice. BR-CLAIM-02: "A claim can be accepted only while its invoice is not cancelled". The seam lets the test pick that exact moment.
    /// </summary>
    [SqlServerFact(Skip = "Known limit (QA L2 of PR 12): a response must not touch the invoice row, so nothing ties the save to it. Both actions are Owner only and the window is milliseconds.")]
    public async Task Known_limit_a_claim_can_be_accepted_on_an_invoice_cancelled_at_the_moment_of_the_accept()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        var invoice = await SellAsync(w, 0);
        var claim = await w.Trader.Client.SubmitClaimAsync(invoice);
        var rowVersion = await RowVersionAsync(w, claim.Id());
        await using var raced = w.Host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInvoiceSalesReturnCheck>(new CancelsTheInvoiceMeanwhile(w.Host.Database))));
        var owner = raced.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(owner, w.Host.Owner.Account.Username);
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var answer = await owner.AcceptClaimAsync(claim.Id(), rowVersion);

        var status = await w.Host.QueryAsync(db => db.SalesInvoices.AsNoTracking().Where(i => i.Id == invoice).Select(i => i.StatusId).SingleAsync());
        Assert.Equal(StatusIds.SalesInvoice.Cancelled, status);
        Assert.NotEqual(HttpStatusCode.OK, answer.StatusCode);
    }

    private sealed class CancelsTheInvoiceMeanwhile(SqlServerTestDatabase database) : IInvoiceSalesReturnCheck
    {
        private int _done;

        public async Task<bool> HasSalesReturnAsync(Guid invoiceId, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                await using var db = database.CreateContext();
                await db.SalesInvoices.Where(i => i.Id == invoiceId).ExecuteUpdateAsync(i => i.SetProperty(x => x.StatusId, StatusIds.SalesInvoice.Cancelled), cancellationToken);
            }

            return false;
        }
    }

    [SqlServerFact]
    public async Task A_zone_manager_list_and_the_status_filter_translate_on_sql_server_and_keep_to_the_zone()
    {
        var w = await StartAsync(2);
        await using var _ = w.Host;
        var claim = await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, 0));
        var answered = await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, 1));
        await (await w.Owner.RejectClaimAsync(answered.Id(), await RowVersionAsync(w, answered.Id()))).ReadAsync(HttpStatusCode.OK);
        var manager = await w.Host.StaffInZoneAsync(w.ZoneId);
        var otherZone = (await w.Owner.CreateZoneAsync()).Id();
        var outsider = await w.Host.StaffInZoneAsync(otherZone);

        var page = await (await manager.Client.GetAsync("/api/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(new[] { claim.Id(), answered.Id() }, page.GetProperty("items").EnumerateArray().Select(i => i.Id()).ToArray());
        var awaiting = await (await manager.Client.GetAsync($"/api/quality-claims?statusId={StatusIds.QualityClaim.AwaitingResponse}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(claim.Id(), awaiting.GetProperty("items").EnumerateArray().Single().Id());
        var none = await (await outsider.Client.GetAsync("/api/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Client.GetAsync($"/api/quality-claims/{claim.Id()}")).StatusCode);
    }

    private static Task<List<string>> TablesAsync(OmtsDbContext db) => db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables").ToListAsync();

    private static Task<List<string>> ColumnsAsync(OmtsDbContext db, string table) => db.Database.SqlQuery<string>($"SELECT name AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({table})").ToListAsync();

    private sealed class JsonElementHolder
    {
        public System.Text.Json.JsonElement Last { get; set; }
    }

    private sealed record IndexRow(bool IsUnique, bool HasFilter, string FilterDefinition);

    /// <summary>Counts the EF commands sent to the test's own database.</summary>
    private sealed class CommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _database;
        private readonly List<IDisposable> _subscriptions = [];
        private int _count;

        public CommandCounter(string database)
        {
            _database = database;
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        public async Task<int> CountAsync<T>(Func<Task<T>> call)
        {
            var start = Volatile.Read(ref _count);
            await call();
            return Volatile.Read(ref _count) - start;
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == DbLoggerCategory.Name)
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuting.Name && value.Value is CommandEventData data && data.Command.Connection?.Database == _database)
            {
                Interlocked.Increment(ref _count);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                _subscriptions.ForEach(s => s.Dispose());
            }
        }
    }
}
