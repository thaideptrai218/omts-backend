using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Storage;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>Stops the first upload after it was stored, until released: a request in the middle of its work.</summary>
public sealed class UploadHold
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    public void Arm() => _armed = 1;

    public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public void Release() => _released.TrySetResult();

    internal async Task PassAsync()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _reached.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}

public sealed class HeldStorage(IFileStorage inner, UploadHold hold) : IFileStorage
{
    public async Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default)
    {
        var stored = await inner.UploadAsync(file, category, cancellationToken);
        await hold.PassAsync();
        return stored;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) => inner.OpenReadAsync(key, cancellationToken);

    public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default) => inner.GetReadUrlAsync(key, lifetime, cancellationToken);

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);

    public Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default) => inner.ListAsync(category, cancellationToken);
}

/// <summary>The API with a held upload, and a notification dispatcher that can fail after the photos are stored.</summary>
public sealed class CycleLogsQaFaultFactory : AuthApiFactory
{
    public UploadHold Hold { get; } = new();

    public bool NotifyFails { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IFileStorage>(provider => new HeldStorage(provider.GetRequiredService<LocalFileStorage>(), Hold));
            services.AddScoped<NotificationDispatcher>();
            services.AddScoped<INotificationDispatcher>(provider => new SwitchedDispatcher(provider.GetRequiredService<NotificationDispatcher>(), this));
        });
    }

    private sealed class SwitchedDispatcher(INotificationDispatcher inner, CycleLogsQaFaultFactory owner) : INotificationDispatcher
    {
        public Task NotifyAsync(IReadOnlyCollection<Guid> recipientAccountIds, NotificationContent content, CancellationToken cancellationToken) =>
            owner.NotifyFails ? throw new InvalidOperationException("Test: the database stopped answering.") : inner.NotifyAsync(recipientAccountIds, content, cancellationToken);
    }
}

/// <summary>The API with the Azure Blob provider, on a container of its own.</summary>
public sealed class CycleLogsQaBlobFactory : AuthApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Storage:Provider", "AzureBlob");
        builder.UseSetting("Storage:AzureBlob:ConnectionString", Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName));
        builder.UseSetting("Storage:AzureBlob:Container", $"omts-test-{Guid.NewGuid():N}");
    }
}

/// <summary>QA of PR C4: what is stored, served, refused and swept.</summary>
public class CycleLogsQaTestsFiles(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Category = FileCategories.CultivationLogPhotos;

    private static ConcurrencyConflictException Conflict => new(new InvalidOperationException("Test: changed by another request."));

    private async Task<LogFields> EditOfAsync(System.Text.Json.JsonElement log) => new()
    {
        Reason = "wrong photo", RowVersion = await factory.LogVersionAsync(log.Id()), KeepPhotoKeys = log.PhotoKeys(), Photos = [],
    };

    private static Task<int> RunJobAsync(AuthApiFactory api, int daysAhead = 2) =>
        api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(daysAhead));

    private static async Task<string> StoreOrphanAsync(AuthApiFactory api)
    {
        var validated = FileValidator.Validate([new IncomingFile("file", Png.Length, new MemoryStream(Png))], FileRules.Photos);
        var stored = await api.Services.GetRequiredService<IFileStorage>().UploadAsync(validated.Files[0], Category);
        Assert.True(stored.Succeeded);
        return stored.Key!;
    }

    [Fact]
    public async Task Each_photo_of_a_save_is_stored_whole_and_its_link_serves_its_own_bytes()
    {
        var setup = await factory.CycleAsync();
        var (first, second, third) = (Sized(Jpeg, 4096), Sized(Png, 2048), Sized(Jpeg, 1024));
        (first[4000], second[2000], third[1000]) = (1, 2, 3);

        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId, new LogFields { Photos = [first, second, third] });

        var anonymous = factory.CreateClient();
        var photos = log.GetProperty("photos");
        Assert.Equal(first, await anonymous.GetByteArrayAsync(photos[0].Text("url")));
        Assert.Equal(second, await anonymous.GetByteArrayAsync(photos[1].Text("url")));
        Assert.Equal(third, await anonymous.GetByteArrayAsync(photos[2].Text("url")));
    }

    [Fact]
    public async Task The_link_of_a_removed_photo_stops_a_kept_one_works_and_a_signature_opens_only_its_own_file()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        var (removedUrl, keptUrl) = (log.GetProperty("photos")[0].Text("url"), log.GetProperty("photos")[1].Text("url"));
        var (removedKey, keptKey) = (log.PhotoKeys()[0], log.PhotoKeys()[1]);
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(keptUrl + "0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(removedUrl.Replace(removedKey, keptKey))).StatusCode);

        var edit = await EditOfAsync(log) with { KeepPhotoKeys = [keptKey] };
        await (await owner.PutLogAsync(log.Id(), edit)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(removedUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(keptUrl)).StatusCode);

        await (await owner.RemoveLogAsync(log.Id(), "wrong raft", await factory.LogVersionAsync(log.Id()))).ReadAsync(HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(keptUrl)).StatusCode);
    }

    [Fact]
    public async Task A_key_of_a_log_in_another_zone_cannot_be_kept_and_a_repeated_key_counts_once_for_the_cap()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        var elsewhere = await owner.StartCycleAsync((await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id());
        var foreign = await owner.RecordLogAsync(elsewhere.Id());
        var edit = await EditOfAsync(log);
        var keys = log.PhotoKeys();

        Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { KeepPhotoKeys = [keys[0], foreign.PhotoKeys()[0]] })).InvalidFieldsAsync());
        Assert.Equal(keys, (await owner.GetLogAsync(log.Id())).PhotoKeys());
        Assert.Equal(foreign.PhotoKeys(), (await owner.GetLogAsync(foreign.Id())).PhotoKeys());

        Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { Photos = Enumerable.Repeat(Png, 3).ToList() })).InvalidFieldsAsync());
        var three = await (await owner.PutLogAsync(log.Id(), edit with { KeepPhotoKeys = [keys[1], keys[1], keys[1]], Photos = Enumerable.Repeat(Png, 2).ToList() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(Enumerable.Range(1, 3), three.GetProperty("log").PhotoSlots());
    }

    public static TheoryData<byte[]> NotPhotos => new() { WebP, Pdf, "GIF89a-not-allowed"u8.ToArray(), Array.Empty<byte>() };

    [Theory]
    [MemberData(nameof(NotPhotos))]
    public async Task A_correction_refuses_what_a_new_log_refuses_and_stores_nothing(byte[] notPhoto)
    {
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        var before = factory.StoredFiles(Category).Count;

        var answer = await setup.Owner.Client.PutLogAsync(log.Id(), await EditOfAsync(log) with { Photos = [Jpeg, notPhoto] });

        Assert.Equal(["Photos"], await answer.InvalidFieldsAsync());
        Assert.Equal(before, factory.StoredFiles(Category).Count);
    }

    [Fact]
    public async Task A_photo_of_exactly_ten_megabytes_passes_and_a_wrong_part_or_too_many_photos_on_a_correction_do_not()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Sized(Jpeg, 10 * 1024 * 1024)] });
        var edit = await EditOfAsync(log);

        Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { Photos = [Png], PhotoField = "photo" })).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { KeepPhotoKeys = [], Photos = Enumerable.Repeat(Png, 11).ToList() })).InvalidFieldsAsync());
        Assert.Equal(["Photos"], await (await owner.PutLogAsync(log.Id(), edit with { Photos = [Sized(Png, 10 * 1024 * 1024 + 1)] })).InvalidFieldsAsync());
    }

    // No request may end in a 500: nothing in these bodies comes from the server.
    [Fact]
    public async Task Malformed_requests_are_client_errors()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId);
        var logs = $"/api/cultivation-cycles/{setup.CycleId}/cultivation-logs";
        var one = $"/api/cultivation-logs/{log.Id()}";

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await owner.PostAsync(logs, new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await owner.PutAsync(one, new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);

        var noBoundary = new ByteArrayContent("junk"u8.ToArray());
        noBoundary.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        var answers = new[]
        {
            await owner.PostAsync(logs, noBoundary),
            await owner.PostAsync(logs, new ByteArrayContent("junk"u8.ToArray()) { Headers = { ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=x") } }),
            await owner.PostAsync($"{one}/remove", null),
            await owner.PostAsync($"{one}/remove", new StringContent("[]", Encoding.UTF8, "application/json")),
        };

        foreach (var answer in answers)
        {
            Assert.True((int)answer.StatusCode is >= 400 and < 500, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
        }

        Assert.Single((await owner.GetLogAsync(log.Id())).PhotoKeys());
    }

    // MSG-GEN-01, not a 500, also beyond the form limits of ASP.NET Core (1024 values).
    [Fact]
    public async Task Values_above_the_form_limits_answer_msg_gen_01_and_change_nothing()
    {
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        var owner = setup.Owner.Client;

        Assert.Equal(["Notes"], await (await owner.PostLogAsync(setup.CycleId, new LogFields { Notes = new string('x', 5 * 1024 * 1024) })).InvalidFieldsAsync());

        var keys = Enumerable.Range(0, 1100).Select(i => $"cultivation-log-photos/2026/11/{i}.jpg").ToList();
        var answer = await owner.PutLogAsync(log.Id(), await EditOfAsync(log) with { KeepPhotoKeys = keys });

        // No client holding ten keys sends this: the framework's anonymous field is enough, as long as nothing changes.
        Assert.Equal(["Request"], await answer.InvalidFieldsAsync());
        Assert.Equal(log.PhotoKeys(), (await owner.GetLogAsync(log.Id())).PhotoKeys());
    }

    // The whole flow on the provider of production: a real signed link, a deleted blob answers 404, the job keeps what is in use.
    [AzuriteFact]
    public async Task On_blob_storage_a_log_serves_its_photos_a_removed_one_stops_and_the_job_keeps_those_in_use()
    {
        using var api = new CycleLogsQaBlobFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var (first, second) = (Sized(Jpeg, 2048), Sized(Png, 1024));
        using var outside = new HttpClient();

        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = [first, second] });
        var (firstUrl, secondUrl) = (log.GetProperty("photos")[0].Text("url"), log.GetProperty("photos")[1].Text("url"));
        Assert.Equal(first, await outside.GetByteArrayAsync(firstUrl));
        Assert.Equal(second, await outside.GetByteArrayAsync(secondUrl));

        var edit = new LogFields { Reason = "wrong photo", RowVersion = await api.LogVersionAsync(log.Id()), KeepPhotoKeys = [log.PhotoKeys()[1]], Photos = [] };
        await (await owner.PutLogAsync(log.Id(), edit)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.NotFound, (await outside.GetAsync(firstUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await outside.GetAsync(secondUrl)).StatusCode);
        Assert.Equal(0, await RunJobAsync(api));
        Assert.Equal(second, await outside.GetByteArrayAsync(secondUrl));
    }

    [Fact]
    public async Task A_failing_removal_after_a_failed_upload_still_answers_msg_gen_03_and_the_clean_up_sweeps_the_file()
    {
        using var api = new FileFaultApiFactory();
        var setup = await api.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        api.Faults.UploadsBeforeFailure = 1;
        api.Faults.DeleteThrows = true;

        var answer = await (await setup.Owner.Client.PostLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] })).ReadAsync(HttpStatusCode.BadGateway);

        Assert.Equal("MSG-GEN-03", answer.Text("code"));
        Assert.Equal(1, await api.LogCountAsync(setup.CycleId));
        Assert.Equal(2, StoredKeys(api, Category).Count);
        api.Faults.DeleteThrows = false;
        Assert.Equal(1, await RunJobAsync(api));
        Assert.Equal(log.PhotoKeys().Order(), StoredKeys(api, Category)!);
    }

    // The guard against another environment's photos also holds back the leftovers of a failed first log: they wait for the first saved log, whose audit row lets the job run.
    [Fact]
    public async Task A_photo_left_by_a_failed_first_log_waits_for_the_first_saved_log()
    {
        using var api = new FileFaultApiFactory();
        var setup = await api.CycleAsync();
        api.Faults.UploadsBeforeFailure = 1;
        api.Faults.DeleteThrows = true;
        await (await setup.Owner.Client.PostLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] })).ReadAsync(HttpStatusCode.BadGateway);
        api.Faults.UploadsBeforeFailure = null;
        api.Faults.DeleteThrows = false;

        Assert.Equal(0, await RunJobAsync(api));
        Assert.Single(StoredKeys(api, Category));

        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);

        Assert.Equal(1, await RunJobAsync(api));
        Assert.Equal(log.PhotoKeys().Order(), StoredKeys(api, Category)!);
    }

    [Fact]
    public async Task A_refused_removal_keeps_the_log_and_its_files_and_writes_no_audit_row()
    {
        using var api = new FileFaultApiFactory();
        var setup = await api.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        api.Faults.FailNextSave(Conflict);

        Assert.Equal("MSG06", await (await setup.Owner.Client.RemoveLogAsync(log.Id(), "wrong raft", await api.LogVersionAsync(log.Id()))).ErrorCodeAsync());

        Assert.Equal(log.PhotoKeys().Order(), StoredKeys(api, Category)!);
        Assert.Equal(1, await api.LogCountAsync(setup.CycleId));
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "CultivationLog.Removed")));
    }

    // A correction committed but answered with an error: the saved row keeps its files, the replaced file waits for the clean-up and only that one is swept.
    [Fact]
    public async Task A_correction_lost_after_its_commit_loses_no_file_of_the_saved_log()
    {
        using var api = new FileFaultApiFactory();
        var setup = await api.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        var edit = new LogFields { Reason = "wrong photo", RowVersion = await api.LogVersionAsync(log.Id()), KeepPhotoKeys = [log.PhotoKeys()[1]], Photos = [Jpeg] };
        api.Faults.FailNextSaveAfterCommit(new InvalidOperationException("Test: the reply was lost."));

        Assert.Equal(HttpStatusCode.InternalServerError, (await setup.Owner.Client.PutLogAsync(log.Id(), edit)).StatusCode);

        var read = await setup.Owner.Client.GetLogAsync(log.Id());
        Assert.All(read.PhotoKeys(), key => Assert.Contains(key, StoredKeys(api, Category)));
        Assert.Contains(log.PhotoKeys()[0], StoredKeys(api, Category));
        Assert.Equal(1, await RunJobAsync(api));
        Assert.Equal(read.PhotoKeys().Order(), StoredKeys(api, Category)!);
    }

    // A file of a save still running is young, so the job waits for it; the listing comes before the references, so the save may also end in between.
    [Fact]
    public async Task The_clean_up_leaves_a_photo_being_saved_at_that_moment()
    {
        using var api = new CycleLogsQaFaultFactory();
        var setup = await api.CycleAsync();
        api.Hold.Arm();

        var saving = setup.Owner.Client.PostLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        await api.Hold.Reached;
        Assert.Equal(0, await RunJobAsync(api, daysAhead: 0));
        api.Hold.Release();

        var log = (await (await saving).ReadAsync(HttpStatusCode.Created)).GetProperty("log");
        Assert.Equal(log.PhotoKeys().Order(), StoredKeys(api, Category)!);
    }

    // The job's own promise: nothing is deleted when the database does not look like this storage's own. Every deployed database holds the cooperative from its
    // bootstrap, so that alone would let a new database on the storage of another environment sweep every log photo older than the grace period.
    [Fact]
    public async Task A_database_that_never_held_a_log_does_not_delete_log_photos_it_does_not_know()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var photoOfAnotherEnvironment = await StoreOrphanAsync(api);

        var deleted = await RunJobAsync(api);

        Assert.Equal(0, deleted);
        Assert.Equal([photoOfAnotherEnvironment], StoredKeys(api, Category));
        Assert.Contains(api.Logs.Lines, l => l.StartsWith("Warning:") && l.Contains(Category) && l.Contains("never held a cultivation log"));
    }

    // UploadedFileSaver and GFR-01: a change that is not saved leaves no file behind, also when it fails between the uploads and the save.
    [Fact]
    public async Task A_failure_between_the_uploads_and_the_save_leaves_no_uploaded_photo()
    {
        using var api = new CycleLogsQaFaultFactory();
        var setup = await api.CycleAsync();
        api.NotifyFails = true;

        var answer = await setup.Owner.Client.PostLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });

        Assert.Equal(HttpStatusCode.InternalServerError, answer.StatusCode);
        Assert.Equal(0, await api.LogCountAsync(setup.CycleId));
        Assert.Empty(StoredKeys(api, Category));
    }

    /// <summary>Runs <paramref name="send"/> up to its first stored photo, lets <paramref name="meanwhile"/> happen and returns the answer.</summary>
    private static async Task<HttpResponseMessage> WithHeldUploadAsync(CycleLogsQaFaultFactory api, Func<Task<HttpResponseMessage>> send, Func<Task> meanwhile)
    {
        api.Hold.Arm();
        var sending = send();
        await api.Hold.Reached;
        await meanwhile();
        api.Hold.Release();
        return await sending;
    }

    // The cycle as it is after the uploads decides, not the one the request first saw: the guards still hold over a long upload, and its photos go.
    [Theory]
    [InlineData("closed", "MSG-GEN-09")]
    [InlineData("cancelled", "NOT_FOUND")]
    [InlineData("new start date", "MSG-GEN-01")]
    public async Task A_cycle_that_changes_during_the_upload_of_a_log_refuses_it_and_no_photo_stays(string change, string code)
    {
        using var api = new CycleLogsQaFaultFactory();
        var setup = await api.CycleAsync();

        var answer = await WithHeldUploadAsync(
            api,
            () => setup.Owner.Client.PostLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-5), Photos = [Jpeg, Png] }),
            async () =>
            {
                switch (change)
                {
                    case "closed":
                        await api.CloseCycleAsync(setup.CycleId);
                        break;
                    case "cancelled":
                        var version = await api.IssueRowVersionAsync<CultivationCycle>(setup.CycleId);
                        await (await setup.Owner.Client.CancelCycleAsync(setup.CycleId, version)).ReadAsync(HttpStatusCode.NoContent);
                        break;
                    default:
                        await api.UpdateAsync<CultivationCycle>(setup.CycleId, c => c.StartDate = Today);
                        break;
                }
            });

        Assert.Equal(code, await answer.ErrorCodeAsync());
        Assert.Equal(0, await api.LogCountAsync(setup.CycleId));
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "CULTIVATION_LOG_RECORDED")));
    }

    [Fact]
    public async Task A_log_removed_during_the_upload_of_its_correction_stays_removed_and_no_new_photo_stays()
    {
        using var api = new CycleLogsQaFaultFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId);

        var answer = await WithHeldUploadAsync(
            api,
            () => owner.PutLogAsync(log.Id(), new LogFields { Reason = "a better photo", KeepPhotoKeys = log.PhotoKeys(), Photos = [Png] }),
            async () => await (await owner.RemoveLogAsync(log.Id(), "wrong raft", null)).ReadAsync(HttpStatusCode.NoContent));

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Equal(0, await api.LogCountAsync(setup.CycleId));
        Assert.Empty(StoredKeys(api, Category));
    }

    // The file of a photo removed meanwhile is gone, so that request cannot be honoured: nothing changes and the client reads the log again.
    [Fact]
    public async Task A_photo_to_keep_that_another_correction_removed_during_the_upload_refuses_the_correction()
    {
        using var api = new CycleLogsQaFaultFactory();
        var setup = await api.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { Photos = [Jpeg, Png] });
        var keys = log.PhotoKeys();

        var answer = await WithHeldUploadAsync(
            api,
            () => owner.PutLogAsync(log.Id(), new LogFields { Notes = "slow", Reason = "a better photo", KeepPhotoKeys = [keys[0]], Photos = [Png] }),
            async () => await (await owner.PutLogAsync(log.Id(), new LogFields { Notes = "saved meanwhile", Reason = "wrong photo", KeepPhotoKeys = [keys[1]], Photos = [] })).ReadAsync(HttpStatusCode.OK));

        Assert.Equal(["Photos"], await answer.InvalidFieldsAsync());
        var read = await owner.GetLogAsync(log.Id());
        Assert.Equal("saved meanwhile", read.Text("notes"));
        Assert.Equal([keys[1]], read.PhotoKeys());
        Assert.Equal([keys[1]], StoredKeys(api, Category));
    }
}
