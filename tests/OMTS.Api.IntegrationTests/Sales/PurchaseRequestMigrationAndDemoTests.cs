using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>The migration PurchaseRequests Up, Down, Up on SQL Server, and the demo step and its reruns.</summary>
public class PurchaseRequestMigrationAndDemoTests
{
    private const string MigrationBefore = "20261104174337_SalesCatalogueV2";

    [SqlServerFact]
    public async Task Up_down_up_keeps_the_rest_of_the_database_and_leaves_no_pending_migration()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var cooperative = new Cooperative { FullName = "HTX" };
        db.Cooperatives.Add(cooperative);
        await db.SaveChangesAsync();

        await db.Database.MigrateAsync();
        await ExecuteAsync(database, "INSERT INTO Products (Id, Name, UnitPrice, StatusId, CreatedAt) VALUES ('00000000-0000-0000-0000-000000000001', N'Hàu', 60000, 101, SYSDATETIMEOFFSET());");
        await ExecuteAsync(database, $"INSERT INTO Customers (Id, CooperativeId, Name, PhoneCheckedByStaff, CreatedAt) VALUES ('00000000-0000-0000-0000-000000000002', '{cooperative.Id}', N'A', 0, SYSDATETIMEOFFSET());");
        await ExecuteAsync(database, "INSERT INTO PurchaseRequests (Id, CustomerId, ProductId, RequestedKg, PickupDate, SentAt, StatusId, CreatedAt) VALUES ('00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', 1.5, '2026-11-05', SYSDATETIMEOFFSET(), 3201, SYSDATETIMEOFFSET());");
        Assert.Equal(4, await db.Statuses.CountAsync(s => s.Group == StatusGroups.PurchaseRequest));
        Assert.Equal(4, await db.StatusTransitions.CountAsync(t => t.Id >= 180 && t.Id <= 183));

        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(0, await db.Statuses.CountAsync(s => s.Group == StatusGroups.PurchaseRequest));
        Assert.Equal(1, await db.Products.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.Customers.AsNoTracking().CountAsync());

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await db.PurchaseRequests.AsNoTracking().CountAsync());
        Assert.Equal(4, await db.Statuses.CountAsync(s => s.Group == StatusGroups.PurchaseRequest));
    }

    [SqlServerFact]
    public async Task The_demo_has_a_request_in_each_state_and_a_rerun_adds_nothing_and_undoes_nothing()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
                [BootstrapSettings.OwnerDisplayNameKey] = "Nguyễn Văn Hải",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            });
            Assert.Equal(DeploymentBootstrapper.Success, (await SeedAsync(api)).ExitCode);

            using (var scope = api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
                var statuses = await db.PurchaseRequests.AsNoTracking().Select(r => r.StatusId).OrderBy(s => s).ToListAsync();
                Assert.Equal(
                    [StatusIds.PurchaseRequest.AwaitingResponse, StatusIds.PurchaseRequest.Accepted, StatusIds.PurchaseRequest.Declined, StatusIds.PurchaseRequest.Cancelled],
                    statuses);
                var sendings = await db.Notifications.AsNoTracking().CountAsync(n => n.Channel == NotificationChannel.Sms);
                Assert.Equal(2, sendings);
                Assert.Equal(4, await db.Notifications.AsNoTracking().CountAsync(n => n.Type == NotificationTypes.PurchaseRequestNew));
            }

            // What the trader and the Owner may have done since: the demo trader cancelled the request still awaiting.
            using (var scope = api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
                var awaiting = await db.PurchaseRequests.SingleAsync(r => r.StatusId == StatusIds.PurchaseRequest.AwaitingResponse);
                awaiting.StatusId = StatusIds.PurchaseRequest.Cancelled;
                await db.SaveChangesAsync();
            }

            var before = await CountsAsync(api);
            Assert.Equal(DeploymentBootstrapper.Success, (await SeedAsync(api)).ExitCode);
            Assert.Equal(before, await CountsAsync(api));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task<BootstrapOutcome> SeedAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(seedDemoData: true, CancellationToken.None);
    }

    private static async Task<(int Requests, int Cancelled, int Notifications, int Audit)> CountsAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        return (
            await db.PurchaseRequests.CountAsync(),
            await db.PurchaseRequests.CountAsync(r => r.StatusId == StatusIds.PurchaseRequest.Cancelled),
            await db.Notifications.CountAsync(),
            await db.AuditEvents.CountAsync());
    }

    private static async Task ExecuteAsync(SqlServerTestDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
