using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>The optional photos of a lot: GFR-01 and MSG-GEN-03 when one cannot be uploaded, signed links, and the orphan file clean-up on their own category.</summary>
public class HarvestLotPhotoTests
{
    private const string Category = FileCategories.HarvestLotPhotos;

    private static ConcurrencyConflictException Conflict => new(new InvalidOperationException("Test: changed by another request."));

    private static List<string> KeysOf(System.Text.Json.JsonElement lot) => lot.GetProperty("photos").EnumerateArray().Select(p => p.Text("key")).ToList();

    private static Task<int> RunJobAsync(IServiceProvider services, int daysAhead = 2) =>
        services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(daysAhead));

    private static async Task<string> StoreAsync(IServiceProvider services, byte[] content)
    {
        var validated = FileValidator.Validate([new IncomingFile("file", content.Length, new MemoryStream(content))], FileRules.Photos);
        var stored = await services.GetRequiredService<IFileStorage>().UploadAsync(validated.Files[0], Category);
        Assert.True(stored.Succeeded);
        return stored.Key!;
    }

    [Fact]
    public async Task Photos_are_optional_stored_in_their_category_with_signed_links_and_in_slot_order()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        Assert.Empty((await owner.HarvestLotAsync(raft.RaftId)).GetProperty("photos").EnumerateArray());

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png] });

        var photos = lot.GetProperty("photos").EnumerateArray().ToList();
        Assert.Equal([1, 2], photos.Select(p => p.GetProperty("sortOrder").GetInt32()));
        Assert.All(photos, p =>
        {
            Assert.StartsWith($"{Category}/", p.Text("key"));
            Assert.NotEmpty(p.Text("url"));
        });
        Assert.Equal(KeysOf(lot).Order(), StoredKeys(factory, Category)!);
        var signed = await factory.CreateClient().GetAsync(photos[0].Text("url"));
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
    }

    [Fact]
    public async Task Ten_photos_are_saved_and_the_eleventh_a_wrong_format_or_another_part_is_refused_and_stores_nothing()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        var tooMany = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = Enumerable.Repeat(Jpeg, 11).ToList() })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "A lot holds at most 10 photos."), (tooMany.Text("field"), tooMany.Text("hint")));
        var wrong = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Pdf] })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Photos", wrong.Text("field"));
        var part = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { PhotoField = "picture", Photos = [Jpeg] })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "Send each photo in a part named photos."), (part.Text("field"), part.Text("hint")));
        Assert.Empty(StoredKeys(factory, Category));

        var ten = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = Enumerable.Repeat(Jpeg, 10).ToList() });
        Assert.Equal(10, KeysOf(ten).Count);
    }

    [Fact]
    public async Task A_refused_field_stores_no_photo()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync();

        await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Cages = "0", Photos = [Jpeg] })).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Empty(StoredKeys(factory, Category));
    }

    [Fact]
    public async Task A_photo_that_cannot_be_uploaded_saves_nothing_tells_nobody_and_removes_the_ones_before_it()
    {
        using var factory = new FileFaultApiFactory();
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        factory.Faults.UploadsBeforeFailure = 1;

        var answer = await (await manager.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png] })).ReadAsync(HttpStatusCode.BadGateway);

        Assert.Equal(("MSG-GEN-03", "MSG05"), (answer.Text("code"), answer.Text("legacyCode")));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
        Assert.Empty(StoredKeys(factory, Category));
        Assert.Equal(0, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "HARVEST_LOT_RECORDED")));
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Created")));

        factory.Faults.UploadsBeforeFailure = null;
        var saved = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png] });
        Assert.Equal(KeysOf(saved).Order(), StoredKeys(factory, Category)!);
    }

    [Fact]
    public async Task A_save_that_conflicts_after_the_uploads_answers_msg06_removes_them_and_the_next_send_works()
    {
        using var factory = new FileFaultApiFactory();
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);

        factory.Faults.FailNextSave(Conflict);
        var answer = await manager.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });

        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        Assert.Empty(StoredKeys(factory, Category));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
        Assert.Single(KeysOf(await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] })));
    }

    // The unique index on the lot code: the loser takes the next attempt with its uploads, and nothing of the failed attempt is written.
    [Fact]
    public async Task A_lost_race_on_the_code_keeps_the_photos_tells_once_and_writes_one_audit_row()
    {
        using var factory = new FileFaultApiFactory();
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        factory.Faults.FailNextSave(new DuplicateRecordException(new InvalidOperationException("Test: the code was taken.")));

        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });

        Assert.Equal(KeysOf(lot), StoredKeys(factory, Category)!);
        Assert.Equal(1, await factory.LotCountAsync(raft.RaftId));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Created" && e.RecordId == lot.Id())));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Created")));
        Assert.Single(await factory.NoticesAsync("HARVEST_LOT_RECORDED", lot.Id()));
        Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "HARVEST_LOT_RECORDED")));
    }

    [Fact]
    public async Task The_category_is_scanned_photos_in_use_stay_and_an_old_unreferenced_one_is_swept()
    {
        Assert.Contains(Category, OrphanFileCleanupService.Categories);
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, Png] });
        var orphan = await StoreAsync(factory.Services, Png);

        Assert.Equal(0, await RunJobAsync(factory.Services, daysAhead: 0));
        Assert.Contains(orphan, StoredKeys(factory, Category));

        Assert.Equal(1, await RunJobAsync(factory.Services));

        Assert.Equal(KeysOf(lot).Order(), StoredKeys(factory, Category)!);
    }

    // A database that never held a lot is not this storage's own: its photos are another environment's. No cooperative says the same.
    [Fact]
    public async Task A_database_that_never_held_a_lot_or_a_cooperative_deletes_nothing_and_says_so()
    {
        using var empty = new AuthApiFactory();
        var photo = await StoreAsync(empty.Services, Png);
        Assert.Equal(0, await RunJobAsync(empty.Services));
        Assert.Equal([photo], StoredKeys(empty, Category));
        Assert.Contains(empty.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains(Category) && l.Contains("the database holds no cooperative"));

        using var noLot = new AuthApiFactory();
        var raft = await noLot.SeededRaftAsync();
        var kept = await StoreAsync(noLot.Services, Png);
        Assert.Equal(0, await RunJobAsync(noLot.Services));
        Assert.Equal([kept], StoredKeys(noLot, Category));
        Assert.Contains(noLot.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains(Category) && l.Contains("never held a harvest lot"));

        // A lot with no photo was recorded: no photo in use, but it is this database's, so the old file goes.
        await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        Assert.Equal(1, await RunJobAsync(noLot.Services));
        Assert.Empty(StoredKeys(noLot, Category));
    }
}
