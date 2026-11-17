using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223 on a real SQL Server: the status catalog, the migration and the deadlock handling of UnitOfWork.</summary>
public class PurchaseRequestQaCatalogAndMigrationSqlServerTests
{
    private const string MigrationBefore = "20261105093736_EmployeesV2";

    // UnitOfWork.cs came with this PR as in PR #22, and this branch has no test of it: a real SQL Server deadlock (1205) is a conflict for the loser, never a 500.
    [SqlServerFact]
    public async Task A_real_deadlock_is_a_conflict_for_the_loser_and_the_other_save_goes_through()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var (a, b) = ((await owner.CreateCustomerAsync()).Id(), (await owner.CreateCustomerAsync()).Id());
        using var first = host.Api.Services.CreateScope();
        using var second = host.Api.Services.CreateScope();
        var db1 = first.ServiceProvider.GetRequiredService<OMTS.Infrastructure.Persistence.OmtsDbContext>();
        var db2 = second.ServiceProvider.GetRequiredService<OMTS.Infrastructure.Persistence.OmtsDbContext>();
        db1.Database.AutoSavepointsEnabled = false;
        db2.Database.AutoSavepointsEnabled = false;
        var bOfFirst = await db1.Customers.SingleAsync(c => c.Id == b);
        var aOfSecond = await db2.Customers.SingleAsync(c => c.Id == a);
        await using var tx1 = await db1.Database.BeginTransactionAsync();
        await using var tx2 = await db2.Database.BeginTransactionAsync();
        await db1.Database.ExecuteSqlInterpolatedAsync($"UPDATE Customers SET Address = N'one' WHERE Id = {a}");
        await db2.Database.ExecuteSqlInterpolatedAsync($"UPDATE Customers SET Address = N'two' WHERE Id = {b}");
        bOfFirst.Note = "from the first";
        aOfSecond.Note = "from the second";

        var saves = new[]
        {
            first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(),
            second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(),
        };
        var outcomes = await Task.WhenAll(saves.Select(async save =>
        {
            try
            {
                await save;
                return "saved";
            }
            catch (Exception exception)
            {
                return exception.GetType().Name;
            }
        }));

        Assert.Contains("ConcurrencyConflictException", outcomes);
        Assert.Contains("saved", outcomes);
    }

    // §I.3 STD-PURCHASE-REQUEST as BR-SALE-01 and BR-SALE-02 state it: the moves the routes make are the rows the database holds.
    [SqlServerFact]
    public async Task The_status_group_and_its_transitions_match_the_srs()
    {
        await using var host = await FarmingHost.StartAsync();

        var statuses = await host.Api.DbAsync(db => db.Statuses.AsNoTracking().Where(s => s.Group == StatusGroups.PurchaseRequest).OrderBy(s => s.Id).ToListAsync());
        Assert.Equal(
            // Awaiting staff is the staffing step before the response (decision of 31/10/2026); the SRS has to add it.
            [(3201, "AWAITING_RESPONSE", "Awaiting response", true, false), (3202, "ACCEPTED", "Accepted", false, false), (3203, "DECLINED", "Declined", false, true), (3204, "CANCELLED", "Cancelled", false, true),
                (3205, "AWAITING_STAFF", "Awaiting staff confirmation", false, false)],
            statuses.Select(s => (s.Id, s.Code, s.DisplayName, s.IsInitial, s.IsFinal)).ToList());

        var ids = statuses.Select(s => s.Id).ToList();
        var transitions = await host.Api.DbAsync(db => db.StatusTransitions.AsNoTracking().Where(t => ids.Contains(t.FromStatusId) || ids.Contains(t.ToStatusId)).OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(
            [(3201, 3202, "Cooperative Owner", false), (3201, 3203, "Cooperative Owner", true), (3201, 3204, "Trader", false), (3202, 3204, "Trader", false),
                (3201, 3205, "Cooperative Owner", false), (3205, 3203, "Cooperative Owner", true), (3205, 3204, "Trader", false), (3205, 3202, "Cooperative Owner", false)],
            transitions.Select(t => (t.FromStatusId, t.ToStatusId, t.AllowedPerformers, t.ReasonRequired)).ToList());
    }

    // Up, Down, Up with rows of every kind present, and the model matches the last migration.
    [SqlServerFact]
    public async Task Up_down_up_with_requests_and_their_notifications_present_leaves_the_notifications_and_no_model_change()
    {
        await using var host = await FarmingHost.StartAsync();
        var (owner, trader, productId) = await PurchaseRequestSqlServerTests.WorldAsync(host);
        var request = await trader.Client.PlaceAsync(productId);
        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();
        var notifications = await host.Api.DbAsync(db => db.Notifications.CountAsync());
        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationBefore);

        Assert.Equal(notifications, await db.Notifications.AsNoTracking().CountAsync());
        await using (var connection = new SqlConnection(host.Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = 'PurchaseRequests'";
            Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
        }

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        // Awaiting response, Accepted, Declined, Cancelled and Awaiting staff (cage orders, OMTS-223).
        Assert.Equal(5, await db.Statuses.CountAsync(s => s.Group == StatusGroups.PurchaseRequest));
    }
}
