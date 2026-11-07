using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Jobs;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// The photo of a recognition that was never saved has no record pointing to it: the orphan file clean-up removes it
/// after the grace period, and keeps the photos of measurements (a voided one included).
/// </summary>
public class WaterMeterPhotoCleanupTests
{
    [Fact]
    public async Task An_abandoned_recognition_is_cleaned_up_and_the_photos_of_measurements_are_kept()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var saved = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var voided = await manager.PhotoKeyAsync(z.Zone.Id);
        var abandoned = await manager.PhotoKeyAsync(z.Zone.Id);
        var recent = await manager.PhotoKeyAsync(z.Zone.Id);
        Assert.Equal(HttpStatusCode.Created, (await z.RecordAsync(7m, 20m, photoKey: saved)).StatusCode);
        var toVoid = await z.RecordAsync(7m, 20m, client: manager, photoKey: voided);
        Assert.Equal(HttpStatusCode.Created, toVoid.StatusCode);
        var voidedId = (await toVoid.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).Id();
        Assert.Equal(HttpStatusCode.OK, (await manager.VoidAsync(voidedId)).StatusCode);
        foreach (var key in new[] { saved, voided, abandoned })
        {
            File.SetLastWriteTimeUtc(Path.Combine(api.StorageRoot, key), DateTime.UtcNow.AddDays(-2));
        }

        var deleted = await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(api.StorageRoot, abandoned)));
        foreach (var key in new[] { saved, voided, recent })
        {
            Assert.True(File.Exists(Path.Combine(api.StorageRoot, key)), key);
        }

        // The key of a cleaned-up photo can no longer be recorded with.
        Assert.Equal(HttpStatusCode.BadRequest, (await z.RecordAsync(7m, 20m, client: manager, photoKey: abandoned)).StatusCode);
    }
}
