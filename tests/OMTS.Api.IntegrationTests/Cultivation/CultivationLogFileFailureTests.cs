using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>GFR-01 and MSG-GEN-03 on the photos of a log: an upload failure saves nothing and leaves no file; a failed save loses no current photo.</summary>
public class CultivationLogFileFailureTests
{
    private const string Category = FileCategories.CultivationLogPhotos;

    private static ConcurrencyConflictException Conflict => new(new InvalidOperationException("Test: changed by another request."));

    [Fact]
    public async Task A_photo_that_cannot_be_uploaded_saves_nothing_tells_nobody_and_removes_the_ones_before_it()
    {
        using var factory = new FileFaultApiFactory();
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        factory.Faults.UploadsBeforeFailure = 2;

        var answer = await (await manager.Client.PostLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png, Jpeg] })).ReadAsync(HttpStatusCode.BadGateway);

        Assert.Equal(("MSG-GEN-03", "MSG05"), (answer.Text("code"), answer.Text("legacyCode")));
        Assert.Equal(0, await factory.LogCountAsync(setup.CycleId));
        Assert.Empty(StoredKeys(factory, Category));
        Assert.Equal(0, await factory.QueryAsync(db => db.Notifications.CountAsync()));
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "CultivationLog.Created")));

        // The other entries are the client's to keep: the same request can be sent again.
        factory.Faults.UploadsBeforeFailure = null;
        var saved = await manager.Client.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png, Jpeg] });
        Assert.Equal(saved.PhotoKeys().Order(), StoredKeys(factory, Category)!);
    }

    [Fact]
    public async Task A_save_that_fails_after_the_uploads_removes_them_and_the_next_try_works()
    {
        using var factory = new FileFaultApiFactory();
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);

        factory.Faults.FailNextSave(Conflict);
        Assert.Equal("MSG06", await (await manager.Client.PostLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] })).ErrorCodeAsync());

        Assert.Equal(0, await factory.LogCountAsync(setup.CycleId));
        Assert.Empty(StoredKeys(factory, Category));
        Assert.Equal(0, await factory.QueryAsync(db => db.Notifications.CountAsync()));
        await manager.Client.RecordLogAsync(setup.CycleId);
        Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync()));
    }

    [Fact]
    public async Task A_correction_whose_photo_cannot_be_uploaded_changes_nothing()
    {
        using var factory = new FileFaultApiFactory();
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId, new LogFields { Notes = "before" });
        var version = await factory.LogVersionAsync(log.Id());
        factory.Faults.UploadsBeforeFailure = 1;

        var edit = new LogFields { Notes = "after", Reason = "wrong photos", RowVersion = version, Photos = [Png, Png] };
        Assert.Equal("MSG-GEN-03", await (await setup.Owner.Client.PutLogAsync(log.Id(), edit)).ErrorCodeAsync());

        var read = await setup.Owner.Client.GetLogAsync(log.Id());
        Assert.Equal("before", read.Text("notes"));
        Assert.Equal(log.PhotoKeys(), read.PhotoKeys());
        Assert.Equal(log.PhotoKeys().Order(), StoredKeys(factory, Category)!);
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "CultivationLog.Corrected")));
    }

    [Fact]
    public async Task A_failed_correction_keeps_the_current_photos_and_a_saved_one_removes_the_replaced_ones()
    {
        using var factory = new FileFaultApiFactory();
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        var edit = new LogFields { Reason = "wrong photo", RowVersion = await factory.LogVersionAsync(log.Id()), KeepPhotoKeys = [log.PhotoKeys()[1]], Photos = [Jpeg] };

        factory.Faults.FailNextSave(Conflict);
        Assert.Equal("MSG06", await (await setup.Owner.Client.PutLogAsync(log.Id(), edit)).ErrorCodeAsync());
        Assert.Equal(log.PhotoKeys().Order(), StoredKeys(factory, Category)!);
        Assert.Equal(log.PhotoKeys(), (await setup.Owner.Client.GetLogAsync(log.Id())).PhotoKeys());

        var saved = (await (await setup.Owner.Client.PutLogAsync(log.Id(), edit)).ReadAsync(HttpStatusCode.OK)).GetProperty("log");

        Assert.Equal(saved.PhotoKeys().Order(), StoredKeys(factory, Category)!);
        Assert.DoesNotContain(log.PhotoKeys()[0], saved.PhotoKeys());
        Assert.Contains(log.PhotoKeys()[1], saved.PhotoKeys());
    }

    // The reference check knows the column of the category: a reply lost after the commit must not delete the saved photos.
    [Fact]
    public async Task A_save_failing_after_its_commit_keeps_the_files_the_log_points_to()
    {
        using var factory = new FileFaultApiFactory();
        var setup = await factory.CycleAsync();

        factory.Faults.FailNextSaveAfterCommit(new InvalidOperationException("Test: the reply was lost."));
        Assert.Equal(HttpStatusCode.InternalServerError, (await setup.Owner.Client.PostLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] })).StatusCode);

        var saved = Assert.Single(await setup.Owner.Client.ListLogsAsync(setup.CycleId));
        Assert.Equal(saved.PhotoKeys().Order(), StoredKeys(factory, Category)!);
        Assert.Contains(factory.Logs.Lines, l => l.Contains("is kept: the failed save was committed"));
    }

    [Fact]
    public async Task A_failing_removal_hides_no_answer_and_is_left_to_the_clean_up()
    {
        using var factory = new FileFaultApiFactory();
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        factory.Faults.DeleteThrows = true;

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.Client.RemoveLogAsync(log.Id(), "recorded on the wrong raft", ""))!.StatusCode);

        Assert.Single(StoredKeys(factory, Category));
        Assert.Contains(factory.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains("could not be removed after it was replaced"));

        // No log is left at all, and the clean-up still sweeps it (BR: the last log of the cooperative can be removed).
        factory.Faults.DeleteThrows = false;
        Assert.Equal(1, await factory.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Empty(StoredKeys(factory, Category));
    }
}
