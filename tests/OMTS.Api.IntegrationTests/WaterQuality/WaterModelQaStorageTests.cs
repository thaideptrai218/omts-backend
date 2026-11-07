using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Jobs;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of the meter photo flow: the OCR answers of WATER-01.2, the shape of a photo key on both storage providers, the
/// orphan job and the races around a re-send. A skipped test names its finding and says why it stays open
/// (see reports/qa-261104-water-model.md).
/// </summary>
public class WaterModelQaStorageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task A_gateway_that_throws_is_a_failed_recognition_with_a_key_and_every_value_typed_by_hand()
    {
        await using var api = new WaterApiFactory(services: s => UseOcr(s, new StubOcr((_, _) => throw new HttpRequestException("gateway down"))));
        var z = await api.ZoneAsync();

        var response = await z.RecognizeAsync();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ocr = body.GetProperty("ocr");
        Assert.Equal("Failed", ocr.Text("status"));
        Assert.Equal(["PH", "SALINITY", "TEMPERATURE"], ocr.GetProperty("unread").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("MSG-WATER-01", ocr.GetProperty("warning").Text("code"));
        Assert.Contains("pH, salinity and temperature", ocr.GetProperty("warning").Text("text"));
        Assert.Empty(body.GetProperty("valuesOutside").EnumerateArray());
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
        Assert.Equal(HttpStatusCode.Created, (await z.RecordAsync(7m, 20m, photoKey: body.Text("photoKey"))).StatusCode);
    }

    [Theory]
    [InlineData(true, false, false, "salinity and temperature", "SALINITY,TEMPERATURE")]
    [InlineData(false, true, true, "pH", "PH")]
    [InlineData(true, true, false, "temperature", "TEMPERATURE")]
    [InlineData(false, false, false, "pH, salinity and temperature", "PH,SALINITY,TEMPERATURE")]
    public async Task A_partial_answer_names_exactly_the_values_not_read(bool ph, bool salinity, bool temperature, string named, string unread)
    {
        var reading = OcrResult.Read(
            ph ? new OcrReading<decimal>(7.2m, 0.9) : null,
            salinity ? new OcrReading<decimal>(20m, 0.9) : null,
            temperature ? new OcrReading<decimal>(26m, 0.9) : null,
            null);
        await using var api = new WaterApiFactory(services: s => UseOcr(s, new StubOcr((_, _) => Task.FromResult(reading))));
        var z = await api.ZoneAsync();

        var ocr = (await (await z.RecognizeAsync()).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ocr");

        Assert.Equal("Success", ocr.Text("status"));
        Assert.Equal(unread, string.Join(',', ocr.GetProperty("unread").EnumerateArray().Select(v => v.GetString())));
        Assert.Equal($"OMTS could not read {named} from the photo. Please look at the meter and type in the missing values.", ocr.GetProperty("warning").Text("text"));
        Assert.Equal(ph ? JsonValueKind.Object : JsonValueKind.Null, ocr.GetProperty("ph").ValueKind);
        Assert.Equal(temperature ? JsonValueKind.Object : JsonValueKind.Null, ocr.GetProperty("temperature").ValueKind);
    }

    [Theory]
    [InlineData("<html><script>alert(1)</script></html>", "image/png")]
    [InlineData("%PDF-1.4 meter", "image/png")]
    [InlineData("", "image/jpeg")]
    public async Task A_file_that_is_not_an_image_is_refused_on_the_photo_and_nothing_is_stored(string content, string contentType)
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm(System.Text.Encoding.ASCII.GetBytes(content), contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal("Photo", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.False(Directory.Exists(Path.Combine(api.StorageRoot, FileCategories.WaterMeterPhotos)));
    }

    [Fact]
    public async Task A_recognize_request_that_is_not_multipart_is_refused_without_a_500()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsJsonAsync($"{z.Base}/recognize", new { photo = "x" });

        Assert.True(response.StatusCode is HttpStatusCode.UnsupportedMediaType or HttpStatusCode.BadRequest, $"{(int)response.StatusCode}");
    }

    // The owner folder widened the key pattern: no key may leave the storage folder or reach another file, in any category.
    [Fact]
    public async Task No_key_shape_reaches_a_file_of_another_category_or_leaves_the_storage_folder_on_local_storage()
    {
        await using var api = new WaterApiFactory();
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var photo = await UploadAsync(storage, FileCategories.RaftPhotos);
        var license = await UploadAsync(storage, FileCategories.ZoneLicenses);
        var outside = Path.Combine(Path.GetDirectoryName(api.StorageRoot)!, $"omts-outside-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(outside, Png);

        try
        {
            foreach (var key in new[]
                     {
                         $"raft-photos/../{license}",
                         $"raft-photos/2026/../../{license}",
                         $"../{Path.GetFileName(outside)}",
                         $"raft-photos/../../{Path.GetFileName(outside)}",
                         $"raft-photos/x/../../../{Path.GetFileName(outside)}",
                         outside,
                         $"..\\{Path.GetFileName(outside)}",
                         $"raft-photos/%2e%2e/{license}",
                         photo.Replace('/', '\\'),
                         photo.ToUpperInvariant(),
                         photo + "\n",
                         photo + "::$DATA",
                         $"/{photo}",
                         $"raft-photos/a/b/{photo["raft-photos/".Length..]}",
                         $"raft-photos/A_B/{photo["raft-photos/".Length..]}",
                         $"raft-photos/../raft-photos/{photo["raft-photos/".Length..]}",
                     })
            {
                await using (var read = await storage.OpenReadAsync(key))
                {
                    Assert.True(read is null, key);
                }

                Assert.False(await storage.DeleteAsync(key), key);
            }

            Assert.True(File.Exists(outside));
            Assert.True(File.Exists(Path.Combine(api.StorageRoot, photo)));
            Assert.True(File.Exists(Path.Combine(api.StorageRoot, license)));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.ListAsync("raft-photos/../zone-licenses"));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.ListAsync("water-meter-photos/abc"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // Only a meter photo has an owner folder, and that folder is an account id. Any other nested file is one this provider
    // did not write: it is not a key, so it is never listed and never cleaned up.
    [Fact]
    public async Task A_nested_folder_is_a_stored_key_only_for_meter_photos()
    {
        await using var api = new WaterApiFactory();
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var nestedRaftPhoto = await WriteFileAsync(api, $"{FileCategories.RaftPhotos}/backup/2026/11/{Guid.NewGuid():N}.png");
        var nestedOwnerShape = await WriteFileAsync(api, $"{FileCategories.RaftPhotos}/{Guid.NewGuid():N}/2026/11/{Guid.NewGuid():N}.png");
        await WriteFileAsync(api, $"{FileCategories.WaterMeterPhotos}/backup/2026/11/{Guid.NewGuid():N}.png");
        var meterPhoto = await WriteFileAsync(api, $"{FileCategories.WaterMeterPhotos}/{Guid.NewGuid():N}/2026/11/{Guid.NewGuid():N}.png");

        Assert.Empty(await storage.ListAsync(FileCategories.RaftPhotos));
        Assert.Equal([meterPhoto], (await storage.ListAsync(FileCategories.WaterMeterPhotos)).Select(f => f.Key));
        foreach (var notAKey in new[] { nestedRaftPhoto, nestedOwnerShape })
        {
            await using var read = await storage.OpenReadAsync(notAKey);
            Assert.Null(read);
        }
    }

    // A category other than a meter photo's owner folder is one folder, as it was before owner folders: a slash becomes a dash.
    [Theory]
    [InlineData("../../evil")]
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("a/../../../evil")]
    [InlineData("a/b/c/d")]
    [InlineData("A b/C:d")]
    [InlineData("raft-photos/0123456789abcdef0123456789abcdef")]
    [InlineData("water-meter-photos/not-an-account")]
    public async Task A_category_never_writes_outside_its_folder_and_is_one_folder(string category)
    {
        await using var api = new WaterApiFactory();
        var storage = api.Services.GetRequiredService<IFileStorage>();

        try
        {
            var key = await UploadAsync(storage, category);

            var full = Path.GetFullPath(Path.Combine(api.StorageRoot, key));
            Assert.StartsWith(Path.GetFullPath(api.StorageRoot) + Path.DirectorySeparatorChar, full);
            Assert.DoesNotContain("..", key);
            Assert.Equal(4, key.Split('/').Length);
            await using var read = await storage.OpenReadAsync(key);
            Assert.NotNull(read);
        }
        catch (ArgumentException)
        {
            // Refused: a category with no usable name.
        }
    }

    // One photo has one spelling, whatever the provider.
    [Fact]
    public async Task Other_spellings_of_a_stored_key_are_refused_on_local_storage()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();

        Assert.Empty(await AcceptedSpellingsAsync(z));
    }

    // The SDK removes a "." path segment, so "water-meter-photos/{me}/./2026/11/{guid}.png" would open the same blob under
    // another text; that text must never pass the check of BR-WATER-02 and be saved as the photo key.
    [AzuriteFact]
    public async Task Other_spellings_of_a_stored_key_are_refused_on_blob_storage()
    {
        await using var api = BlobApi();
        var z = await api.ZoneAsync();

        Assert.Empty(await AcceptedSpellingsAsync(z));
    }

    // The check sits in front of every provider: a key that is not the stored form never reaches the storage at all.
    [Fact]
    public async Task A_key_that_is_not_the_stored_form_is_refused_on_the_photo_before_the_storage_is_touched()
    {
        var storage = new CountingStorage();
        await using var api = new WaterApiFactory(services: s =>
        {
            s.RemoveAll<IFileStorage>();
            s.AddSingleton<IFileStorage>(storage);
        });
        var z = await api.ZoneAsync();
        var folder = $"{FileCategories.WaterMeterPhotos}/{z.OwnerAccount.Id:N}";
        var stored = $"{folder}/2026/11/{Guid.NewGuid():N}.png";

        foreach (var key in new[] { $"{folder}/./2026/11/{Guid.NewGuid():N}.png", stored + "/", stored + "\n", stored.Replace("/2026/11/", "\\2026\\11\\"), stored[..^4] + ".PNG", $"{folder}/{Guid.NewGuid():N}.png", new string('a', 5000) })
        {
            var refused = await z.RecordAsync(7m, 20m, photoKey: key);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("MSG-GEN-01", await refused.ErrorCodeAsync());
            Assert.Equal("Photo", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        Assert.Equal(0, storage.Reads);

        // The stored form does reach the storage, which knows no such file: the same answer.
        var unknown = await z.RecordAsync(7m, 20m, photoKey: stored);

        Assert.Equal("Photo", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(1, storage.Reads);
    }

    // The provider has the check too, for a key that comes from anywhere else.
    [AzuriteFact]
    public async Task On_blob_storage_another_spelling_of_a_stored_key_opens_nothing()
    {
        await using var api = BlobApi();
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var storage = api.Services.GetRequiredService<IFileStorage>();

        await using (var stored = await storage.OpenReadAsync(key))
        {
            Assert.NotNull(stored);
        }

        await using var dotted = await storage.OpenReadAsync(Dotted(z, key));
        Assert.Null(dotted);
    }

    // BR-WATER-02: one blob is the photo of one measurement, under any spelling.
    [AzuriteFact]
    public async Task One_photo_cannot_serve_two_measurements_through_another_spelling()
    {
        await using var api = BlobApi();
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);

        var first = await z.RecordAsync(7m, 20m, photoKey: key);
        var second = await z.RecordAsync(7m, 20m, photoKey: Dotted(z, key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    // The column can only hold the stored key, so the orphan job sees the blob as used and keeps the proof of the measurement.
    [AzuriteFact]
    public async Task The_orphan_job_keeps_the_photo_of_a_measurement_after_another_spelling_of_its_key_was_refused()
    {
        await using var api = BlobApi();
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await z.RecordAsync(7m, 20m, photoKey: Dotted(z, key))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await z.RecordAsync(7m, 20m, photoKey: key)).StatusCode);

        await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));

        await using var read = await api.Services.GetRequiredService<IFileStorage>().OpenReadAsync(key);
        Assert.True(read is not null, "The photo of a saved measurement was deleted by the orphan job.");
    }

    // A photo that was refused under another spelling stays usable under its own, and its read link opens.
    [AzuriteFact]
    public async Task The_photo_link_of_a_measurement_saved_after_another_spelling_was_refused_opens()
    {
        await using var api = BlobApi();
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await z.RecordAsync(7m, 20m, photoKey: Dotted(z, key))).StatusCode);

        var saved = await z.RecordAsync(7m, 20m, photoKey: key);

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        using var http = new HttpClient();
        var opened = await http.GetAsync((await saved.Content.ReadFromJsonAsync<JsonElement>()).Text("photoUrl"));
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
    }

    // The folder of the account is the only proof of ownership: no spelling may climb out of it, on the provider without a key check.
    [AzuriteFact]
    public async Task No_spelling_reaches_the_photo_of_another_account_on_blob_storage()
    {
        await using var api = BlobApi();
        var z = await api.ZoneAsync();
        var (manager, managerAccount) = await api.StaffAsync(z.Zone);
        var keyOfTheManager = await manager.PhotoKeyAsync(z.Zone.Id);
        var inFolders = keyOfTheManager[$"{FileCategories.WaterMeterPhotos}/".Length..];
        var mine = $"{FileCategories.WaterMeterPhotos}/{z.OwnerAccount.Id:N}/";
        Assert.StartsWith(managerAccount.Id.ToString("N"), inFolders);

        var accepted = new List<string>();
        foreach (var climb in new[]
                 {
                     "../", "..\\", ".%2E/", "%2E%2E/", "%2e./", ".\t./", ".\n./", ".\r./", ". ./", ".­./", ".​./", "．．/", "․․/", ".\\./.\\./", "./.\\", "/../",
                 })
        {
            var response = await z.RecordAsync(7m, 20m, photoKey: mine + climb + inFolders);
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                accepted.Add($"{(int)response.StatusCode}: {climb}");
            }
        }

        Assert.Empty(accepted);
    }

    [AzuriteFact]
    public async Task On_blob_storage_the_flow_works_and_an_abandoned_photo_of_an_owner_folder_is_swept_after_the_wait()
    {
        await using var api = BlobApi();
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var used = await manager.PhotoKeyAsync(z.Zone.Id);
        var abandoned = await manager.PhotoKeyAsync(z.Zone.Id);
        var saved = await z.RecordAsync(7m, 20m, client: manager, photoKey: used);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var job = api.Services.GetRequiredService<OrphanFileCleanupJob>();

        Assert.Equal(0, await job.RunOnceAsync(DateTimeOffset.UtcNow));
        Assert.Equal(1, await job.RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2)));

        await using (var gone = await storage.OpenReadAsync(abandoned))
        {
            Assert.Null(gone);
        }

        await using (var kept = await storage.OpenReadAsync(used))
        {
            Assert.NotNull(kept);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await z.RecordAsync(7m, 20m, client: manager, photoKey: abandoned)).StatusCode);
    }

    // The guard of the job, as it stands: while no stored photo is referenced (no measurement yet), nothing is swept.
    [Fact]
    public async Task With_no_measurement_at_all_the_job_keeps_every_photo_and_sweeps_them_after_the_first_measurement()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var abandoned = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        File.SetLastWriteTimeUtc(Path.Combine(api.StorageRoot, abandoned), DateTime.UtcNow.AddDays(-2));
        var job = api.Services.GetRequiredService<OrphanFileCleanupJob>();

        Assert.Equal(0, await job.RunOnceAsync(DateTimeOffset.UtcNow));
        Assert.True(File.Exists(Path.Combine(api.StorageRoot, abandoned)));

        Assert.Equal(HttpStatusCode.Created, (await z.RecordAsync(7m, 20m)).StatusCode);

        Assert.Equal(1, await job.RunOnceAsync(DateTimeOffset.UtcNow));
        Assert.False(File.Exists(Path.Combine(api.StorageRoot, abandoned)));
    }

    // FINDING F-02: the job reads the references once, before its deletes, and a photo older than the wait can still be
    // recorded (recognize gives no expiry). A record saved in that window keeps a measurement without its proof.
    [Fact(Skip = "Accepted: a key must be older than 24 hours and be recorded in the moment the job runs; the job runs once a day.")]
    public async Task F02_a_photo_recorded_while_the_job_runs_is_not_deleted()
    {
        var hook = new OnceHook();
        await using var api = new WaterApiFactory(services: s => DecorateReferences(s, hook));
        var z = await api.ZoneAsync();
        Assert.Equal(HttpStatusCode.Created, (await z.RecordAsync(7m, 20m)).StatusCode);
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var path = Path.Combine(api.StorageRoot, key);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-25));
        HttpResponseMessage? saved = null;
        hook.Action = async () => saved = await z.RecordAsync(7m, 20m, photoKey: key);

        await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.Created, saved!.StatusCode);
        Assert.True(File.Exists(path), "The photo of a saved measurement was deleted by the orphan job.");
    }

    // A re-send that passes the operation check just before the first request commits meets the photo check after it. The
    // photo is used by the record of its own operation, so the answer is the one the client list promises: 200 with that record.
    [Fact]
    public async Task A_re_send_overtaken_between_its_two_checks_answers_the_saved_record()
    {
        var hook = new OnceHook();
        await using var api = new WaterApiFactory(services: s => DecorateRepository(s, hook));
        var z = await api.ZoneAsync();
        var request = new
        {
            clientOperationId = Guid.NewGuid(),
            raftId = z.Raft.Id,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ph = 7m,
            salinity = 20m,
            temperature = 25m,
            photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id),
        };
        HttpResponseMessage? first = null;
        hook.Action = async () => first = await z.Owner.PostAsJsonAsync(z.Base, request);

        var resend = await z.Owner.PostAsJsonAsync(z.Base, request);

        Assert.Equal(HttpStatusCode.Created, first!.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).Id(), (await resend.Content.ReadFromJsonAsync<JsonElement>()).Id());
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    // The look-up finds only this operation: a photo used by another operation is still refused.
    [Fact]
    public async Task A_new_operation_with_a_photo_saved_between_its_two_checks_is_refused_on_the_photo()
    {
        var hook = new OnceHook();
        await using var api = new WaterApiFactory(services: s => DecorateRepository(s, hook));
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        HttpResponseMessage? first = null;
        hook.Action = async () => first = await z.RecordAsync(7m, 20m, clientOperationId: Guid.NewGuid(), photoKey: key);

        var second = await z.RecordAsync(7m, 20m, clientOperationId: Guid.NewGuid(), photoKey: key);

        Assert.Equal(HttpStatusCode.Created, first!.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("Photo", (await second.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    // The same operation id for another zone is a client error, also when it is found only at the second look.
    [Fact]
    public async Task An_operation_of_another_zone_found_between_the_two_checks_is_refused_on_the_operation()
    {
        var hook = new OnceHook();
        await using var api = new WaterApiFactory(services: s => DecorateRepository(s, hook));
        var z = await api.ZoneAsync();
        var otherRaft = await api.RaftAsync(await api.AddZoneAsync(AcsTestKit.NewCode()));
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var operation = Guid.NewGuid();
        HttpResponseMessage? first = null;
        hook.Action = async () => first = await z.RecordAsync(7m, 20m, clientOperationId: operation, photoKey: key);

        var other = await z.Owner.PostAsJsonAsync($"/api/zones/{otherRaft.ZoneId}/water-measurements", new
        {
            clientOperationId = operation,
            raftId = otherRaft.Id,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ph = 7m,
            salinity = 20m,
            temperature = 25m,
            photoKey = key,
        });

        Assert.Equal(HttpStatusCode.Created, first!.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Equal("Client operation id", (await other.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    private static string Dotted(WaterZone z, string key)
    {
        var prefix = $"{FileCategories.WaterMeterPhotos}/{z.OwnerAccount.Id:N}/";
        return $"{prefix}./{key[prefix.Length..]}";
    }

    /// <summary>The spellings of one stored key that the record request accepted.</summary>
    private static async Task<List<string>> AcceptedSpellingsAsync(WaterZone z)
    {
        var prefix = $"{FileCategories.WaterMeterPhotos}/{z.OwnerAccount.Id:N}/";
        var accepted = new List<string>();
        var spellings = new Func<string, string>[]
        {
            key => $"{prefix}./{key[prefix.Length..]}",
            key => $"{prefix}{key[prefix.Length..].Replace("/", "/./")}",
            key => $"{prefix}/{key[prefix.Length..]}",
            key => key + "/",
            key => key + "/.",
            key => key + ".",
            key => key + " ",
            key => key[..^4] + ".PNG",
            key => $"{prefix}{key[prefix.Length..].Replace('/', '\\')}",
            key => key + "?comp=x",
            key => key + "#x",
            key => $"{prefix}{key[prefix.Length..].Replace("/", "%2F")}",
            key => $"{prefix}%2E/{key[prefix.Length..]}",
        };

        foreach (var spelling in spellings)
        {
            var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
            var other = spelling(key);
            var response = await z.RecordAsync(7m, 20m, photoKey: other);
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                accepted.Add($"{(int)response.StatusCode}: {other[prefix.Length..]}");
            }
        }

        return accepted;
    }

    private static WaterApiFactory BlobApi() => new(new Dictionary<string, string>
    {
        ["Storage:Provider"] = "AzureBlob",
        ["Storage:AzureBlob:ConnectionString"] = Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName)!,
        ["Storage:AzureBlob:Container"] = $"omts-test-{Guid.NewGuid():N}",
    });

    /// <summary>A file placed in the storage folder by something other than the provider; returns its path as a key.</summary>
    private static async Task<string> WriteFileAsync(WaterApiFactory api, string relativePath)
    {
        var path = Path.Combine(api.StorageRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Png);
        return relativePath;
    }

    private static async Task<string> UploadAsync(IFileStorage storage, string category)
    {
        var file = FileValidator.Validate([new IncomingFile("photo.png", Png.Length, new MemoryStream(Png))], FileRules.Photos);
        Assert.True(file.IsValid);
        return (await storage.UploadAsync(file.Files[0], category)).Key!;
    }

    private static void UseOcr(IServiceCollection services, IOcrService ocr)
    {
        services.RemoveAll<IOcrService>();
        services.AddSingleton(ocr);
    }

    private static void DecorateReferences(IServiceCollection services, OnceHook hook)
    {
        var original = services.Single(d => d.ServiceType == typeof(IStoredFileReferenceQuery));
        services.Remove(original);
        services.AddScoped<IStoredFileReferenceQuery>(provider =>
            new ReferencesThenHook((IStoredFileReferenceQuery)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), hook));
    }

    private static void DecorateRepository(IServiceCollection services, OnceHook hook)
    {
        var original = services.Single(d => d.ServiceType == typeof(IWaterMeasurementRepository));
        services.Remove(original);
        services.AddScoped<IWaterMeasurementRepository>(provider =>
        {
            var proxy = DispatchProxy.Create<IWaterMeasurementRepository, HookBeforePhotoCheck>();
            var hooked = (HookBeforePhotoCheck)(object)proxy;
            hooked.Inner = (IWaterMeasurementRepository)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
            hooked.Hook = hook;
            return proxy;
        });
    }

    private sealed class StubOcr(Func<Stream, string, Task<OcrResult>> answer) : IOcrService
    {
        public Task<OcrResult> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken) => answer(photo, contentType);
    }

    /// <summary>A storage that holds nothing and counts the reads that reach it.</summary>
    private sealed class CountingStorage : IFileStorage
    {
        private readonly FailingFileStorage _empty = new();
        private int _reads;

        public int Reads => _reads;

        public Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default) =>
            _empty.UploadAsync(file, category, cancellationToken);

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return _empty.OpenReadAsync(key, cancellationToken);
        }

        public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default) =>
            _empty.GetReadUrlAsync(key, lifetime, cancellationToken);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _empty.DeleteAsync(key, cancellationToken);

        public Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default) => _empty.ListAsync(category, cancellationToken);
    }

    /// <summary>An action that runs once, at the moment under test.</summary>
    public sealed class OnceHook
    {
        private Func<Task>? _action;

        public Func<Task>? Action
        {
            set => _action = value;
        }

        public Task RunAsync() => Interlocked.Exchange(ref _action, null)?.Invoke() ?? Task.CompletedTask;
    }

    private sealed class ReferencesThenHook(IStoredFileReferenceQuery inner, OnceHook hook) : IStoredFileReferenceQuery
    {
        public async Task<IReadOnlySet<string>> ReferencedKeysAsync(string category, CancellationToken cancellationToken)
        {
            var keys = await inner.ReferencedKeysAsync(category, cancellationToken);
            if (category == FileCategories.WaterMeterPhotos)
            {
                await hook.RunAsync();
            }

            return keys;
        }

        public Task<bool> IsReferencedAsync(string key, CancellationToken cancellationToken) => inner.IsReferencedAsync(key, cancellationToken);
    }

    /// <summary>Lets another request finish just before the photo check of this one.</summary>
    public class HookBeforePhotoCheck : DispatchProxy
    {
        public IWaterMeasurementRepository Inner { get; set; } = null!;

        public OnceHook Hook { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWaterMeasurementRepository.IsPhotoUsedAsync))
            {
                return AfterHookAsync(args!);
            }

            return targetMethod.Invoke(Inner, args);
        }

        private async Task<bool> AfterHookAsync(object?[] args)
        {
            await Hook.RunAsync();
            return await Inner.IsPhotoUsedAsync((string)args[0]!, (CancellationToken)args[1]!);
        }
    }
}
