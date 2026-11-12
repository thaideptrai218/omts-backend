using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Storage;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingUploadedFileTests;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>A held upload and a clock in one host: a request in the middle of its work when midnight passes in Vietnam.</summary>
public sealed class HarvestLotsQaHeldClockFactory : AuthApiFactory
{
    public UploadHold Hold { get; } = new();

    public CyclesQaClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IFileStorage>(provider => new HeldStorage(provider.GetRequiredService<LocalFileStorage>(), Hold));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>QA of PR H1a: the photos of a lot, what is stored, served, refused and swept, on Local and on Azurite.</summary>
public class HarvestLotsQaTestsFiles
{
    private const string Category = FileCategories.HarvestLotPhotos;
    private const int TenMegabytes = 10 * 1024 * 1024;

    private static ConcurrencyConflictException Conflict => new(new InvalidOperationException("Test: changed by another request."));

    private static Task<int> RunJobAsync(AuthApiFactory api, int daysAhead = 2) =>
        api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(daysAhead));

    private static async Task<string> StoreOrphanAsync(AuthApiFactory api)
    {
        var validated = FileValidator.Validate([new IncomingFile("file", Png.Length, new MemoryStream(Png))], FileRules.Photos);
        var stored = await api.Services.GetRequiredService<IFileStorage>().UploadAsync(validated.Files[0], Category);
        Assert.True(stored.Succeeded);
        return stored.Key!;
    }

    public static TheoryData<string, byte[]> RefusedFiles() => new()
    {
        { "WebP", WebP },
        { "PDF named .jpg", Pdf },
        { "text named .jpg", TextPretendingToBeAFile },
        { "empty", [] },
        { "GIF", "GIF89a\x01\0\x01\0\0\0\0"u8.ToArray() },
    };

    // NFR-14: the real format counts, not the name. The kind of the field reader is told by the first bytes.
    [Theory]
    [MemberData(nameof(RefusedFiles))]
    public async Task A_file_that_is_no_jpeg_or_png_is_refused_with_nothing_stored_and_the_entries_can_be_sent_again(string name, byte[] file)
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;

        var refused = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg, file] })).ReadAsync(HttpStatusCode.BadRequest);

        Assert.True(refused.Text("field") == "Photos", name);
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));
        await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
    }

    [Fact]
    public async Task A_photo_of_10_megabytes_is_saved_and_one_byte_more_is_refused()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;

        var refused = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Sized(Jpeg, TenMegabytes + 1)] })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Photos", refused.Text("field"));
        Assert.Empty(StoredKeys(api, Category));

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Sized(Jpeg, TenMegabytes)] });
        Assert.Single(lot.GetProperty("photos").EnumerateArray());
    }

    // A path in the name of the file stays out of the key: the key is the system's.
    [Theory]
    [InlineData("../../../evil.jpg")]
    [InlineData("..\\..\\evil.jpg")]
    [InlineData("a/b/c.jpg")]
    public async Task A_path_in_a_file_name_stays_out_of_the_storage_key(string fileName)
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var form = new HarvestFields().For(await api.HarvestOrderAsync()).ToForm();
        form.Add(FileContent(Jpeg, "image/jpeg"), "photos", fileName);

        var lot = (await (await raft.Owner.Client.PostAsync($"/api/rafts/{raft.RaftId}/harvest-lots", form)).ReadAsync(HttpStatusCode.Created)).GetProperty("lot");

        var key = lot.GetProperty("photos")[0].Text("key");
        Assert.StartsWith($"{Category}/", key);
        Assert.DoesNotContain("..", key);
        Assert.DoesNotContain("evil", key);
    }

    [Fact]
    public async Task Each_link_serves_its_own_bytes_and_a_changed_or_missing_signature_or_key_serves_nothing()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var (first, second) = (Sized(Jpeg, 4096), Sized(Png, 2048));
        (first[4000], second[2000]) = (1, 2);

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [first, second] });

        var anonymous = api.CreateClient();
        var photos = lot.GetProperty("photos");
        var (firstUrl, secondUrl, firstKey, secondKey) = (photos[0].Text("url"), photos[1].Text("url"), photos[0].Text("key"), photos[1].Text("key"));
        Assert.Equal(first, await anonymous.GetByteArrayAsync(firstUrl));
        Assert.Equal(second, await anonymous.GetByteArrayAsync(secondUrl));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(firstUrl + "0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(firstUrl.Replace(firstKey, secondKey))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(firstUrl.Split('?')[0])).StatusCode);
    }

    // The save fails with a conflict: no lot, no row, no push, no file, and the same send works next.
    [Fact]
    public async Task A_failed_save_pushes_nothing_stores_nothing_and_the_next_send_works()
    {
        using var api = new FileFaultApiFactory();
        var raft = await api.SeededRaftAsync(1);
        var owner = raft.Owner;
        var manager = await api.ManagerAsync(raft.ZoneId);
        var pushes = api.Services.GetRequiredService<FakePushSender>().Inbox;
        var fields = HarvestFields.OfLanes(1).FinishedLanes(1) with { Photos = [Jpeg] };
        api.Faults.FailNextSave(Conflict);

        var answer = await (await manager.Client.PostHarvestAsync(raft.RaftId, fields)).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG06", answer.Text("code"));
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "HARVEST_LOT_RECORDED" || n.Type == "CYCLE_CLOSE_REMINDER")));
        Assert.DoesNotContain(pushes.List(owner.Account.Id.ToString()), m => m.Subject == "New harvest lot");
        Assert.DoesNotContain(pushes.List(manager.Account.Id.ToString()), m => m.Subject == "Reminder to close a cultivation cycle");
        Assert.Null(await api.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == raft.CycleId).Select(c => c.CloseRemindedAt).SingleAsync()));

        await manager.Client.HarvestLotAsync(raft.RaftId, fields);

        Assert.Contains(pushes.List(owner.Account.Id.ToString()), m => m.Subject == "New harvest lot");
        Assert.Single(await api.NoticesAsync("CYCLE_CLOSE_REMINDER", raft.CycleId));
    }

    // A reply lost after the commit: the lot stands, so its photo must stay, however the request answers.
    [Fact]
    public async Task A_reply_lost_after_the_commit_keeps_the_photo_of_the_saved_lot()
    {
        using var api = new FileFaultApiFactory();
        var raft = await api.SeededRaftAsync();
        api.Faults.FailNextSaveAfterCommit(new InvalidOperationException("Test: the reply was lost."));

        await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });

        var keys = await api.QueryAsync(db => db.HarvestLotPhotos.Select(p => p.FileKey).ToListAsync());
        Assert.Single(keys);
        Assert.Equal(keys, StoredKeys(api, Category)!);
    }

    // The guard of the job (README): a database that points to files of the category, none of them stored, is not this storage's own.
    [Fact]
    public async Task The_job_deletes_nothing_when_the_database_points_to_lot_photos_none_of_which_is_stored()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        Assert.True(await api.Services.GetRequiredService<IFileStorage>().DeleteAsync(lot.GetProperty("photos")[0].Text("key")));
        var foreign = await StoreOrphanAsync(api);

        Assert.Equal(0, await RunJobAsync(api));

        Assert.Contains(foreign, StoredKeys(api, Category));
    }

    // A log photo and a lot photo are told apart by their folder: neither job run touches the other's files.
    [Fact]
    public async Task A_lot_photo_and_a_log_photo_are_swept_each_by_its_own_references()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var log = await owner.RecordLogAsync(raft.CycleId);
        var orphan = await StoreOrphanAsync(api);

        Assert.Equal(1, await RunJobAsync(api));

        Assert.DoesNotContain(orphan, StoredKeys(api, Category));
        Assert.Equal(lot.GetProperty("photos")[0].Text("key"), Assert.Single(StoredKeys(api, Category)!));
        Assert.Equal(log.PhotoKeys().Order(), StoredKeys(api, FileCategories.CultivationLogPhotos)!);
    }

    // BR-HARVEST-03: the date of the save. An upload that passes midnight in Vietnam makes a lot of the new day.
    [Fact]
    public async Task A_lot_uploading_over_midnight_in_vietnam_takes_the_date_and_the_code_of_its_save()
    {
        using var api = new HarvestLotsQaHeldClockFactory();
        var owner = (await api.OwnerAsync()).Client;
        var evening = new DateTimeOffset(2026, 10, 26, 23, 59, 0, TimeSpan.FromHours(7));
        api.Clock.At = evening;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV01"))).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody("KV01-B01"))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(new DateOnly(2026, 10, 1)))).Id();
        await owner.RecordSeedingAsync(cycleId, (await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1))).Session([1, 2], new DateOnly(2026, 10, 1)));
        await owner.HarvestLotAsync(raftId);

        api.Hold.Arm();
        var pending = owner.PostHarvestAsync(raftId, new HarvestFields { Photos = [Jpeg] });
        await api.Hold.Reached;
        api.Clock.At = evening.AddMinutes(2);
        api.Hold.Release();

        var lot = (await (await pending).ReadAsync(HttpStatusCode.Created)).GetProperty("lot");
        Assert.Equal(("KV01-B01-20261027", new DateOnly(2026, 10, 27)), (lot.Text("lotCode"), lot.Date("harvestDate")));
    }

    // GFR-02 reads "when they save data". Not graded: "from the user's next action" (BR-FARM-08) lets a save already under way finish. Records what the branch does.
    [Fact]
    public async Task Observation_a_pair_revoked_during_the_upload_does_not_stop_a_save_already_under_way()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);

        api.Hold.Arm();
        var pending = manager.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        await api.Hold.Reached;
        await api.UpdateAsync<AccessGrant>(manager.Grant!.Id, g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; });
        api.Hold.Release();

        Assert.Equal(HttpStatusCode.Created, (await pending).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.PostHarvestAsync(raft.RaftId)).StatusCode);
    }

    [AzuriteFact]
    public async Task On_blob_storage_a_lot_serves_its_photos_only_through_a_signed_link_and_the_job_keeps_them()
    {
        using var api = new CycleLogsQaBlobFactory();
        var raft = await api.SeededRaftAsync();
        var (first, second) = (Sized(Jpeg, 2048), Sized(Png, 1024));
        using var outside = new HttpClient();

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [first, second] });

        var (firstUrl, secondUrl) = (lot.GetProperty("photos")[0].Text("url"), lot.GetProperty("photos")[1].Text("url"));
        Assert.Equal(first, await outside.GetByteArrayAsync(firstUrl));
        Assert.Equal(second, await outside.GetByteArrayAsync(secondUrl));
        Assert.False((await outside.GetAsync(firstUrl.Split('?')[0])).IsSuccessStatusCode, "A lot photo must not be public.");
        Assert.False((await outside.GetAsync(firstUrl[..^3] + "abc")).IsSuccessStatusCode);
        Assert.Equal(0, await RunJobAsync(api));
        Assert.Equal(first, await outside.GetByteArrayAsync(firstUrl));
    }

    [AzuriteFact]
    public async Task On_blob_storage_a_refused_save_removes_its_upload()
    {
        using var api = new CycleLogsQaBlobFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        var refused = await owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1) with { Photos = [Jpeg] });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(await api.Services.GetRequiredService<IFileStorage>().ListAsync(Category));
    }
}

/// <summary>QA of PR H1a on a real Kestrel host, the only one that applies the request size limit.</summary>
public class HarvestLotsQaTestsKestrel(KestrelApiFactory api) : IClassFixture<KestrelApiFactory>
{
    private const long Megabyte = 1024 * 1024;

    private async Task<HttpClient> OnKestrelAsync(string username)
    {
        using var probe = api.CreateKestrelClient();
        var client = new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromMinutes(2) }) { BaseAddress = probe.BaseAddress, Timeout = TimeSpan.FromMinutes(5) };
        var tokens = await AcsTestKit.LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    [Fact]
    public async Task A_request_above_the_limit_answers_400_on_the_photos()
    {
        var raft = await api.SeededRaftAsync();
        using var client = await OnKestrelAsync(raft.Owner.Account.Username);
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/rafts/{raft.RaftId}/harvest-lots")
        {
            Content = new MultipartFormDataContent { { new ZeroContent(106 * Megabyte), "photos", "upload.jpg" } },
        };
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01", "Photos"), (response.StatusCode, problem.Text("code"), problem.Text("field")));
        Assert.Equal(0, await api.LotCountAsync(raft.RaftId));
    }

    // NFR-14: 10 files of 10 MB in one save, with all the other fields.
    [Fact]
    public async Task Ten_photos_of_ten_megabytes_fit_the_request_limit()
    {
        var raft = await api.SeededRaftAsync();
        using var client = await OnKestrelAsync(raft.Owner.Account.Username);
        var photo = Sized(Jpeg, (int)(10 * Megabyte));
        var form = (HarvestFields.OfLanes(1, 2, 3, 4, 5).FinishedLanes(1, 2, 3, 4, 5) with
        {
            Cages = "2147483647", EstimatedKg = "1000000000", Photos = Enumerable.Repeat(photo, 10).ToList(), Order = await api.HarvestOrderAsync(),
        }).ToForm();

        using var response = await client.PostAsync($"api/rafts/{raft.RaftId}/harvest-lots", form);

        var lot = (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("lot");
        Assert.Equal(10, lot.GetProperty("photos").GetArrayLength());
    }

    private sealed class ZeroContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[81920];
            for (var left = length; left > 0; left -= buffer.Length)
            {
                await stream.WriteAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}
