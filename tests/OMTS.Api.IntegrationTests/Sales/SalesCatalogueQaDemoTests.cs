using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: the demo steps rerun on data people have since changed.</summary>
public class SalesCatalogueQaDemoTests
{
    private static async Task<BootstrapOutcome> SeedAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(seedDemoData: true, CancellationToken.None);
    }

    private static async Task<(int Products, int Customers, int Audit)> CountsAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        return (await db.Products.CountAsync(), await db.Customers.CountAsync(), await db.AuditEvents.CountAsync());
    }

    [SqlServerFact]
    public async Task A_rerun_after_a_merge_a_rename_a_new_number_and_a_status_change_adds_nothing_and_undoes_nothing()
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

            // As the client list (§7) says.
            using (var scope = api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
                var products = await db.Products.AsNoTracking().OrderBy(p => p.UnitPrice).Select(p => new { p.Name, p.UnitPrice, p.StatusId }).ToListAsync();
                Assert.Equal(
                    [("Hàu sống loại nhỏ", 45_000m, StatusIds.Usage.InUse), ("Hàu sữa", 60_000m, StatusIds.Usage.InUse), ("Hàu béo", 75_000m, StatusIds.Usage.InUse), ("Hàu thịt tách vỏ", 120_000m, StatusIds.Usage.Discontinued)],
                    products.Select(p => (p.Name, p.UnitPrice, p.StatusId)));
                var offline = await db.Customers.AsNoTracking().Where(c => c.AccountId == null).OrderBy(c => c.PhoneNumber).Select(c => c.PhoneNumber).ToListAsync();
                Assert.Equal(["0900000041", "0900000042", "0900000043", "0900000044", "0900000045"], offline);
                Assert.Equal(["0900000021", "0900000022"], await db.Customers.AsNoTracking().Where(c => c.AccountId != null).OrderBy(c => c.PhoneNumber).Select(c => c.PhoneNumber).ToListAsync());
            }

            // What the Owner and the Zone Managers may have done since.
            using (var scope = api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
                var kept = await db.Customers.SingleAsync(c => c.PhoneNumber == "0900000044");
                (await db.Customers.SingleAsync(c => c.PhoneNumber == "0900000045")).MergedIntoCustomerId = kept.Id;
                (await db.Customers.SingleAsync(c => c.PhoneNumber == "0900000041")).PhoneNumber = "0900000049";
                (await db.Products.SingleAsync(p => p.Name == "Hàu sữa")).Name = "Hàu sữa loại một";
                (await db.Products.SingleAsync(p => p.Name == "Hàu thịt tách vỏ")).StatusId = StatusIds.Usage.InUse;
                await db.SaveChangesAsync();
            }

            var before = await CountsAsync(api);
            var again = await SeedAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, again.ExitCode);
            Assert.Equal(before, await CountsAsync(api));
            using var check = api.Services.CreateScope();
            var after = check.ServiceProvider.GetRequiredService<OmtsDbContext>();
            Assert.NotNull((await after.Customers.SingleAsync(c => c.PhoneNumber == "0900000045")).MergedIntoCustomerId);
            Assert.False(await after.Customers.AnyAsync(c => c.PhoneNumber == "0900000041"));
            Assert.False(await after.Products.AnyAsync(p => p.Name == "Hàu sữa"));
            Assert.Equal(StatusIds.Usage.InUse, (await after.Products.SingleAsync(p => p.Name == "Hàu thịt tách vỏ")).StatusId);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }
}
