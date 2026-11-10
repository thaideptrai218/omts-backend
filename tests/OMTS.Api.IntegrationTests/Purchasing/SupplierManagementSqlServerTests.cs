using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>What only a real database proves: sort and filter on computed figures in SQL, the receipts-and-returns union, and the code backfill.</summary>
public class SupplierManagementSqlServerTests
{
    [SqlServerFact]
    public async Task Sort_and_filter_on_figures_page_in_sql_and_agree_with_the_debt_screen()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;
        var tag = NewCode();
        var expected = new Dictionary<Guid, decimal>();
        foreach (var quantity in new[] { 3, 1, 2 })
        {
            var buyer = world with { SupplierId = await owner.CreateSupplierAsync(name: $"{tag} {quantity}") };
            var receipt = await buyer.CompletedAsync(new LineBody(world.Other, quantity, 10_000));
            await buyer.PayAsync(1_000);
            if (quantity == 3)
            {
                await buyer.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);
            }

            expected[buyer.SupplierId] = await buyer.DebtAsync();
        }

        var first = await (await owner.GetAsync($"/api/suppliers?search={tag}&sortBy=currentDebt&sortDir=desc&page=1&pageSize=2")).ReadAsync(HttpStatusCode.OK);
        var second = await (await owner.GetAsync($"/api/suppliers?search={tag}&sortBy=currentDebt&sortDir=desc&page=2&pageSize=2")).ReadAsync(HttpStatusCode.OK);
        var rows = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).ToList();

        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(expected.Values.OrderDescending(), rows.Select(r => r.Money("currentDebt")));
        Assert.All(rows, r => Assert.Equal(expected[r.Id()], r.Money("currentDebt")));

        var ranged = await (await owner.GetAsync($"/api/suppliers?search={tag}&totalPurchasedFrom=15000&currentDebtTo=20000&purchaseFrom={Today.AddDays(-1):yyyy-MM-dd}&purchaseTo={Today:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, ranged.GetProperty("total").GetInt32());
    }

    [SqlServerFact]
    public async Task The_history_unions_receipts_and_returns_with_paging_on_sql_server()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 2, 10_000));
        await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 7_000)));

        var page = await (await world.Owner.GetAsync($"/api/suppliers/{world.SupplierId}/history?page=1&pageSize=10")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(["Receipt", "Return", "Receipt"], page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal([7_000m, 10_000m, 20_000m], page.GetProperty("items").EnumerateArray().Select(r => r.Money("total")));
    }

    [SqlServerFact]
    public async Task SupplierProfileFields_gives_existing_suppliers_unique_codes_and_new_ones_continue_after_them()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;
        await owner.CreateSupplierAsync();

        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SupplierProfileFields", StringComparison.Ordinal)) - 1]);
        var existing = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM Suppliers").SingleAsync();

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        var codes = await db.Suppliers.AsNoTracking().Select(s => s.Code).ToListAsync();
        Assert.Equal(existing, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^NCC\\d{6}$", code));

        var created = await (await owner.GetAsync($"/api/suppliers/{await owner.CreateSupplierAsync()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal($"NCC{existing + 1:000000}", created.GetProperty("code").GetString());
    }

}
