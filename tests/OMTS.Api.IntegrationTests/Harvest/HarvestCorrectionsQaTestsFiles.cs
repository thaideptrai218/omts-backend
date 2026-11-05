using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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

/// <summary>QA of PR H1b: two forms of one lot, the photos as stored and served, the removals that fail, and the size of a request on Kestrel.</summary>
public class HarvestCorrectionsQaTestsFiles
{
    private const string Category = FileCategories.HarvestLotPhotos;

    private static Task<int> RunJobAsync(AuthApiFactory api) => api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));

    /// <summary>The in-memory provider moves no version on a save, so this stands in for what SQL Server does there. HarvestLotStaleFormSqlServerTests runs the same forms on real versions.</summary>
    private static async Task<string> VersionMovedByTheSaveAsync(AuthApiFactory api, Guid lotId, string read)
    {
        string moved;
        do
        {
            moved = await api.LotVersionAsync(lotId);
        }
        while (moved == read);

        return moved;
    }

    // NFR-09, its acceptance as it reads: two devices open the same record and save one after the other; the second save is blocked with MSG06, and the client list gives that answer for
    // "another save of the lot since this form was read". Device 1 took a photo out; device 2 still keeps it, as a form read before does.
    [Fact]
    public async Task M1_the_second_device_gets_msg06_with_the_new_version_when_the_first_removed_a_photo_its_form_still_keeps()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg, Png] });
        var keys = lot.PhotoKeysOfLot();
        var versionOfBothForms = await api.LotVersionAsync(lot.Id());

        await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"] }, "Wrong photo", versionOfBothForms, [keys[0]]))).ReadAsync(HttpStatusCode.OK);
        var current = await VersionMovedByTheSaveAsync(api, lot.Id(), versionOfBothForms);

        var second = await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"], Cages = "9" }, "Counted again", versionOfBothForms, keys));

        var answer = await second.ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG06", current), (answer.Text("code"), answer.Text("currentRowVersion")));
    }

    // The same on the finished lanes: device 1 un-finishes lane 1, a later lot takes it; device 2 still sends lane 1 as finished, as read.
    [Fact]
    public async Task M1_the_second_device_gets_msg06_with_the_new_version_when_the_first_changed_what_its_form_says_of_a_lane()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1));
        var versionOfBothForms = await api.LotVersionAsync(lot.Id());

        await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(2), "Wrong lane", versionOfBothForms))).ReadAsync(HttpStatusCode.OK);
        var current = await VersionMovedByTheSaveAsync(api, lot.Id(), versionOfBothForms);
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        var second = await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(1, 2).FinishedLanes(1) with { Cages = "9" }, "Counted again", versionOfBothForms));

        var answer = await second.ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG06", current), (answer.Text("code"), answer.Text("currentRowVersion")));
    }

    // The same, with the photo taken out while this request uploads, by a real correction: the comment of the branch's test says this answers MSG06 first.
    [Fact]
    public async Task M1_a_correction_taking_a_photo_out_during_an_upload_answers_msg06_to_the_one_uploading()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg, Png] });
        var keys = lot.PhotoKeysOfLot();
        var version = await api.LotVersionAsync(lot.Id());
        api.Hold.Arm();

        var pending = owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"], Photos = [Jpeg] }, "One more", version, keys));
        await api.Hold.Reached;
        await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"] }, "Wrong photo", version, [keys[0]]))).ReadAsync(HttpStatusCode.OK);
        var current = await VersionMovedByTheSaveAsync(api, lot.Id(), version);
        api.Hold.Release();

        var answer = await (await pending).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG06", current), (answer.Text("code"), answer.Text("currentRowVersion")));
        Assert.Equal([keys[0]], StoredKeys(api, Category));
    }

    // The fix of M2 of the C4 report stays here: a save of another lot of the cycle during an upload refuses nothing.
    [Fact]
    public async Task A_correction_uploading_while_another_lot_of_the_cycle_is_saved_goes_through_with_its_photo()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var other = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        var version = await api.LotVersionAsync(lot.Id());
        api.Hold.Arm();

        var pending = owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["1"], Photos = [Jpeg] }, "One more", version));
        await api.Hold.Reached;
        await api.CorrectLotAsync(owner, other.Id(), HarvestFields.OfLanes(2, 3));
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(4));
        api.Hold.Release();

        var saved = (await (await pending).ReadAsync(HttpStatusCode.OK)).GetProperty("lot");
        Assert.Equal(StoredKeys(api, Category)!, saved.PhotoKeysOfLot());
    }

    // After a correction: a kept link goes on serving its bytes, a removed one stops, and a new one works.
    [Fact]
    public async Task A_kept_link_goes_on_a_removed_link_stops_and_a_removal_stops_them_all()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var (first, second, third) = (Sized(Jpeg, 4096), Sized(Png, 2048), Sized(Png, 1024));
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [first, second] });
        var photos = lot.GetProperty("photos");
        var (firstUrl, secondUrl) = (photos[0].Text("url"), photos[1].Text("url"));
        var anonymous = api.CreateClient();

        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Photos = [third] }, keep: [photos[0].Text("key")])).GetProperty("lot");

        Assert.Equal(first, await anonymous.GetByteArrayAsync(firstUrl));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(secondUrl)).StatusCode);
        var newUrl = saved.GetProperty("photos")[1].Text("url");
        Assert.Equal(third, await anonymous.GetByteArrayAsync(newUrl));

        await api.RemoveLotAsync(owner, lot.Id());

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(firstUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(newUrl)).StatusCode);
    }

    [AzuriteFact]
    public async Task On_blob_storage_a_correction_and_a_removal_leave_exactly_the_photos_in_use()
    {
        using var api = new CycleLogsQaBlobFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var (first, second, third) = (Sized(Jpeg, 2048), Sized(Png, 1024), Sized(Png, 512));
        using var outside = new HttpClient();
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [first, second] });
        var photos = lot.GetProperty("photos");
        var (firstUrl, secondUrl) = (photos[0].Text("url"), photos[1].Text("url"));

        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Photos = [third] }, keep: [photos[0].Text("key")])).GetProperty("lot");

        Assert.Equal(first, await outside.GetByteArrayAsync(firstUrl));
        Assert.False((await outside.GetAsync(secondUrl)).IsSuccessStatusCode, "A removed photo must stop.");
        Assert.Equal(saved.PhotoKeysOfLot().Order(), (await storage.ListAsync(Category)).Select(f => f.Key).Order());
        Assert.Equal(0, await RunJobAsync(api));

        await api.RemoveLotAsync(owner, lot.Id());
        Assert.Empty(await storage.ListAsync(Category));
    }

    // Removals are best effort: a store that fails after the commit does not change the answer, and the job takes the file later.
    [Fact]
    public async Task A_removal_from_the_store_that_fails_after_the_save_changes_no_answer_and_the_job_sweeps_the_file()
    {
        using var api = new FileFaultApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var removed = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["2"], Photos = [Png] });
        var stored = StoredKeys(api, Category).Order().ToList();
        api.Faults.DeleteThrows = true;

        var corrected = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Photos = [Png] })).GetProperty("lot");
        await api.RemoveLotAsync(owner, removed.Id());
        Assert.Contains(lot.PhotoKeysOfLot()[0], StoredKeys(api, Category));
        Assert.Contains(removed.PhotoKeysOfLot()[0], StoredKeys(api, Category));

        api.Faults.DeleteThrows = false;
        await RunJobAsync(api);

        Assert.Equal(corrected.PhotoKeysOfLot(), StoredKeys(api, Category)!);
        Assert.NotEqual(stored, StoredKeys(api, Category));
    }

    // A reply lost after the commit: the corrected lot points to the new photo, so it stays; the old one is left to the job, never lost before the commit.
    [Fact]
    public async Task A_reply_lost_after_the_commit_of_a_correction_keeps_the_new_photo_of_the_saved_lot()
    {
        using var api = new FileFaultApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        api.Faults.FailNextSaveAfterCommit(new ConcurrencyConflictException(new InvalidOperationException("Test: the reply was lost.")));

        var answer = await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Photos = [Png] });

        Assert.Equal("MSG06", await answer.ErrorCodeAsync());
        var keys = await api.QueryAsync(db => db.HarvestLotPhotos.Select(p => p.FileKey).ToListAsync());
        Assert.Contains(Assert.Single(keys), StoredKeys(api, Category));
    }

    // A failed save keeps every file the lot points to and takes back its own.
    [Fact]
    public async Task A_save_that_fails_keeps_the_old_photo_and_removes_only_the_new_one()
    {
        using var api = new FileFaultApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var stored = StoredKeys(api, Category).ToList();
        api.Faults.FailNextSave(new InvalidOperationException("Test: the database stopped answering."));

        await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Photos = [Png] });

        Assert.Equal(stored, StoredKeys(api, Category));
        Assert.Equal(lot.PhotoKeysOfLot(), (await owner.GetHarvestLotAsync(lot.Id())).PhotoKeysOfLot());
    }

    // The job and a database that never held a lot: with lots corrected and removed the audit rows say this is its own, on the rows the removal leaves.
    [Fact]
    public async Task The_job_keeps_every_photo_of_a_lot_on_a_closed_cycle_and_sweeps_an_orphan_beside_them()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });
        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Lanes = ["1"], Photos = [Png] }, keep: lot.PhotoKeysOfLot())).GetProperty("lot");
        await api.CloseCycleAsync(raft.CycleId);
        var validated = FileValidator.Validate([new IncomingFile("file", Png.Length, new MemoryStream(Png))], FileRules.Photos);
        await api.Services.GetRequiredService<IFileStorage>().UploadAsync(validated.Files[0], Category);

        Assert.Equal(1, await RunJobAsync(api));

        Assert.Equal(saved.PhotoKeysOfLot().Order(), StoredKeys(api, Category)!);
    }

    // The most photos: ten on the lot, one replaced; a removal of all at once. NFR-14 reads 10 per save; the lot holds 10 at most (the check constraint): see the report.
    [Fact]
    public async Task Ten_photos_can_be_replaced_one_at_a_time_and_an_eleventh_is_refused_whatever_is_sent()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = Enumerable.Repeat(Jpeg, 10).ToList() });
        var keys = lot.PhotoKeysOfLot();

        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Photos = [Png] }, keep: keys.Skip(1).ToList())).GetProperty("lot");

        Assert.Equal(10, saved.PhotoKeysOfLot().Count);
        Assert.Equal(1, saved.GetProperty("photos")[0].GetProperty("sortOrder").GetInt32());
        Assert.DoesNotContain(keys[0], saved.PhotoKeysOfLot());
        var refused = await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Photos = [Png] }, keep: saved.PhotoKeysOfLot());
        Assert.Equal("Photos", (await refused.ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
    }
}

/// <summary>QA of PR H1b on a real Kestrel host, the only one that applies the request size limit.</summary>
public class HarvestCorrectionsQaTestsKestrel(KestrelApiFactory api) : IClassFixture<KestrelApiFactory>
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
    public async Task A_correction_above_the_limit_answers_400_on_the_photos_and_changes_nothing()
    {
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        using var client = await OnKestrelAsync(raft.Owner.Account.Username);
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/harvest-lots/{lot.Id()}")
        {
            Content = new MultipartFormDataContent { { new ZeroContent(106 * Megabyte), "photos", "upload.jpg" } },
        };
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01", "Photos"), (response.StatusCode, problem.Text("code"), problem.Text("field")));
        Assert.Equal(lot.PhotoKeysOfLot(), (await raft.Owner.Client.GetHarvestLotAsync(lot.Id())).PhotoKeysOfLot());
    }

    [Fact]
    public async Task Ten_new_photos_of_ten_megabytes_with_every_other_field_fit_the_limit_of_a_correction()
    {
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        using var client = await OnKestrelAsync(raft.Owner.Account.Username);
        var photo = FarmingTestKit.Sized(FarmingTestKit.Jpeg, (int)(10 * Megabyte));
        var fields = HarvestFields.OfLanes(1, 2, 3, 4, 5).FinishedLanes(1, 2, 3, 4, 5) with { Photos = Enumerable.Repeat(photo, 10).ToList() };

        using var response = await client.PutAsync($"api/harvest-lots/{lot.Id()}", new HarvestCorrection(fields, new string('x', 1000), await api.LotVersionAsync(lot.Id())).ToForm());

        Assert.Equal(10, (await response.ReadAsync(HttpStatusCode.OK)).GetProperty("lot").GetProperty("photos").GetArrayLength());
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
