using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The orphan file clean-up on the file category of cultivation logs: photos in use stay, removed ones are swept, also when no log is left.</summary>
public class CultivationLogOrphanFileTests
{
    private const string Category = FileCategories.CultivationLogPhotos;

    private static async Task<string> StoreAsync(IServiceProvider services, byte[] content)
    {
        var validated = FileValidator.Validate([new IncomingFile("file", content.Length, new MemoryStream(content))], FileRules.Photos);
        var stored = await services.GetRequiredService<IFileStorage>().UploadAsync(validated.Files[0], Category);
        Assert.True(stored.Succeeded);
        return stored.Key!;
    }

    private static Task<int> RunJobAsync(IServiceProvider services, int daysAhead = 2) =>
        services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(daysAhead));

    [Fact]
    public void The_category_is_scanned_and_written_as_the_storage_writes_it()
    {
        Assert.Contains(Category, OrphanFileCleanupService.Categories);
        Assert.Matches(@"\A[a-z0-9-]+\z", Category);
    }

    [Fact]
    public async Task Every_photo_in_use_stays_and_an_old_unreferenced_one_is_swept_and_a_young_one_waits()
    {
        using var factory = new AuthApiFactory();
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        var orphan = await StoreAsync(factory.Services, Png);

        // Everything is younger than the grace period of this run: nothing moves.
        Assert.Equal(0, await RunJobAsync(factory.Services, daysAhead: 0));
        Assert.Contains(orphan, StoredKeys(factory, Category));

        Assert.Equal(1, await RunJobAsync(factory.Services));

        Assert.Equal(log.PhotoKeys().Order(), StoredKeys(factory, Category)!);
        Assert.Equal(0, await RunJobAsync(factory.Services));
    }

    // Unlike raft photos, every log can be removed in a real database: no reference left does not stop the clean-up.
    [Fact]
    public async Task A_photo_left_after_the_last_log_was_removed_is_swept()
    {
        using var factory = new FileFaultApiFactory();
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        factory.Faults.DeleteThrows = true;
        await setup.Owner.Client.RemoveLogAsync(log.Id(), "wrong raft", "");
        factory.Faults.DeleteThrows = false;
        Assert.Equal(2, StoredKeys(factory, Category).Count);

        Assert.Equal(2, await RunJobAsync(factory.Services));

        Assert.Empty(StoredKeys(factory, Category));
    }

    // A database with no cooperative is not this storage's own: nothing is deleted.
    [Fact]
    public async Task A_database_holding_no_cooperative_deletes_nothing()
    {
        using var factory = new AuthApiFactory();
        var photo = await StoreAsync(factory.Services, Png);

        Assert.Equal(0, await RunJobAsync(factory.Services));

        Assert.Equal([photo], StoredKeys(factory, Category));
        Assert.Contains(factory.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains(Category) && l.Contains("the database holds no cooperative"));
    }

    // One spelling per key: what the blob store lists is exactly what the upload answered, under the category as written.
    [AzuriteFact]
    public async Task Blob_storage_lists_and_deletes_a_photo_under_the_one_key_it_wrote()
    {
        await using var api = new AuthApiFactory().WithWebHostBuilder(b => b
            .UseSetting("Storage:Provider", "AzureBlob")
            .UseSetting("Storage:AzureBlob:ConnectionString", Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName))
            .UseSetting("Storage:AzureBlob:Container", $"omts-test-{Guid.NewGuid():N}"));
        var storage = api.Services.GetRequiredService<IFileStorage>();

        var key = await StoreAsync(api.Services, Png);

        Assert.StartsWith($"{Category}/", key);
        Assert.Equal([key], (await storage.ListAsync(Category)).Select(f => f.Key));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ListAsync(Category.ToUpperInvariant()));
        Assert.True(await storage.DeleteAsync(key));
        Assert.Empty(await storage.ListAsync(Category));
    }

    // The referenced keys are read from SQL Server; every file counts as old here, so only the reference decides.
    [SqlServerFact]
    public async Task Referenced_photos_are_read_from_sql_server_and_kept()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var cycle = await owner.StartCycleAsync(raftId);
        var log = await owner.RecordLogAsync(cycle.Id(), new LogFields { Photos = [Jpeg, Png] });
        await StoreAsync(host.Api.Services, Png);

        Assert.Equal(1, await RunJobAsync(host.Api.Services));

        Assert.Equal(log.PhotoKeys().Order(), host.StoredKeys(Category));
    }
}
