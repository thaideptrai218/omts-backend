using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of PR #58, F3: SQL Server error 547 against a foreign key is read as "a row this save points to was removed meanwhile" by SALE-05 only, on a server in any language;
/// every other save keeps the error it had before 6a (not a concurrency conflict, so a 500 and not MSG06).
/// </summary>
public class SalesInvoicesQaTestsForeignKeySqlServer
{
    // F3: a save outside SALE-05 that points to a missing row is no conflict: AuthService, VerificationService and the retry loops must not read a real bug as a lost race.
    [SqlServerFact]
    public async Task F3_a_save_elsewhere_that_points_to_a_missing_row_is_not_a_concurrency_conflict()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        using var scope = host.Api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Notifications.Add(new Notification { RecipientAccountId = Guid.NewGuid(), Type = "QA", Title = "QA", CreatedAt = DateTimeOffset.UtcNow });

        var failure = await Record.ExceptionAsync(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        Assert.NotNull(failure);
        Assert.IsNotType<ConcurrencyConflictException>(failure);
        Assert.Equal(547, Assert.IsType<SqlException>(failure.GetBaseException()).Number);
        Assert.Equal(0, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "QA")));
    }

    // F3: the product of the sale is removed between its read and its save, on a server whose messages are German. The sale reads again and answers as for a product that never
    // existed (MSG-GEN-01 on the line), as it does on an English server; it is not a 500.
    [SqlServerFact]
    public async Task F3_a_sale_whose_product_is_removed_before_its_save_reads_again_on_a_server_in_another_language()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var productId = (await owner.CreateProductAsync(unitPrice: ListPrice)).Id();
        var customerId = (await owner.CreateCustomerAsync()).Id();
        var manager = await host.StaffInZoneAsync(zoneId);
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));

        var german = new SqlConnectionStringBuilder(host.Database.ConnectionString) { CurrentLanguage = "Deutsch" }.ConnectionString;
        var removal = new ProductRemoval(host.Database.ConnectionString, productId);
        await using var api = host.Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OmtsDb"] = german }));
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<UnitOfWork>();
                services.AddScoped<IUnitOfWork>(provider => new RemovingUnitOfWork(provider.GetRequiredService<UnitOfWork>(), provider.GetRequiredService<OmtsDbContext>(), removal));
            });
        });
        using (var scope = api.Services.CreateScope())
        {
            var language = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Database.SqlQueryRaw<string>("SELECT @@LANGUAGE AS [Value]").SingleAsync();
            Assert.Equal("Deutsch", language);
        }

        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", manager.Client.DefaultRequestHeaders.Authorization!.Parameter);
        var answer = await client.PostInvoiceAsync(Invoice(customerId, [Line(productId, lot.Id(), 10m)], Cash(300_000m), lot.Id()));

        Assert.True(removal.Done);
        Assert.Equal(["Line 1 product"], await answer.InvalidFieldsAsync());
        Assert.Equal(0, await host.QueryAsync(db => db.SalesInvoices.CountAsync()));
    }

    /// <summary>Removes the product once, outside the request, just before the first save of a new invoice: the parallel removal that won.</summary>
    private sealed class ProductRemoval(string connectionString, Guid productId)
    {
        public bool Done { get; private set; }

        public async Task RunOnceAsync()
        {
            if (Done)
            {
                return;
            }

            Done = true;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM [Products] WHERE [Id] = @id";
            command.Parameters.AddWithValue("@id", productId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }

    private sealed class RemovingUnitOfWork(IUnitOfWork inner, OmtsDbContext db, ProductRemoval removal) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Added))
            {
                await removal.RunOnceAsync();
            }

            return await inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);

        public void DiscardChanges() => inner.DiscardChanges();
    }
}
