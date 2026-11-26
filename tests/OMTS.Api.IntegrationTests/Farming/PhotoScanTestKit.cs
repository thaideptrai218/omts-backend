using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Extensions;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Features.Farming.Rafts.FormScan;
using OMTS.Application.Features.Farming.Zones.BoundaryScan;
using OMTS.Application.Features.Scans;
using OMTS.Infrastructure.Ocr;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// A reader that answers a fixed reading (or error), optionally after a gate opens, and records what it was given. It stands in
/// for Gemini in the scan endpoint tests, so no test reaches the network.
/// </summary>
public sealed class ScriptedScanReader : IZoneMapReader, IRaftFormReader
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RawZoneMap Map { get; init; } = SquareHectare;

    public RaftFormReading Form { get; init; } = new(" B-07 ", "24 m x 10 m", "6", "Gần cửa sông", null, "10.1234", "106,5678");

    public string? ErrorCode { get; init; }

    public Exception? Throws { get; init; }

    /// <summary>When set, the read waits for it: the scan stays Pending until the test opens the gate.</summary>
    public Task? Gate { get; init; }

    /// <summary>How many reads started: a scan that waited too long in the queue must not reach the reader.</summary>
    public int Reads => _reads;

    private int _reads;

    public byte[]? ReceivedPhoto { get; private set; }

    public string? ReceivedMimeType { get; private set; }

    /// <summary>Completes when a read has started (so a test knows the worker took the job).</summary>
    public Task Started => _started.Task;

    /// <summary>One 100 m x 100 m zone written as 1,00 ha.</summary>
    public static RawZoneMap SquareHectare { get; } = new("VN-2000", "107°45'",
    [
        new RawMapZone("Khu 1", "(Diện tích: 1,00 ha)",
        [
            new RawMapCorner("K1", "2.315.000,00", "438.000,00", null, null),
            new RawMapCorner("K2", "2.315.000,00", "438.100,00", null, null),
            new RawMapCorner("K3", "2.315.100,00", "438.100,00", null, null),
            new RawMapCorner("K4", "2.315.100,00", "438.000,00", null, null),
        ]),
    ]);

    Task<ScanRead<RawZoneMap>> IZoneMapReader.ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        RunAsync(photo, mimeType, Map);

    Task<ScanRead<RaftFormReading>> IRaftFormReader.ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        RunAsync(photo, mimeType, Form);

    private async Task<ScanRead<T>> RunAsync<T>(ReadOnlyMemory<byte> photo, string mimeType, T value) where T : class
    {
        Interlocked.Increment(ref _reads);
        (ReceivedPhoto, ReceivedMimeType) = (photo.ToArray(), mimeType);
        _started.TrySetResult();
        if (Gate is not null)
        {
            await Gate;
        }

        if (Throws is not null)
        {
            throw Throws;
        }

        return ErrorCode is null ? ScanRead<T>.Read(value) : ScanRead<T>.Failed(ErrorCode);
    }
}

/// <summary>The API with the scan readers replaced, and optionally a small queue, a tight rate limit or another clock.</summary>
public sealed class PhotoScanFactory(
    ScriptedScanReader? reader = null, int? queueCapacity = null, int? maxConcurrent = null, int? zoneMapScansPerHour = null, TimeProvider? clock = null,
    string? provider = null) : AuthApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            if (reader is not null)
            {
                services.AddSingleton<IZoneMapReader>(reader);
                services.AddSingleton<IRaftFormReader>(reader);
            }

            if (clock is not null)
            {
                services.AddSingleton<TimeProvider>(clock);
            }

            services.PostConfigure<OcrProviderOptions>(o =>
            {
                o.Provider = provider ?? o.Provider;
                o.Scans.QueueCapacity = queueCapacity ?? o.Scans.QueueCapacity;
                o.Scans.MaxConcurrent = maxConcurrent ?? o.Scans.MaxConcurrent;
            });
            services.PostConfigure<RateLimitOptions>(o => o.ZoneMapScansPerHour = zoneMapScansPerHour ?? o.ZoneMapScansPerHour);
        });
    }
}

/// <summary>A clock a test moves forward.</summary>
public sealed class MovableClock : TimeProvider
{
    private TimeSpan _shift;

    public void Advance(TimeSpan by) => _shift += by;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _shift;
}

public static class PhotoScanTestKit
{
    public static Task<HttpResponseMessage> PostPhotoAsync(this HttpClient client, string url, byte[]? photo = null, string field = "photo")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo ?? WaterTestKit.Photo("scan"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, field, "scan.png");
        return client.PostAsync(url, form);
    }

    /// <summary>Starts a scan and returns its id; fails the test unless it was accepted (202).</summary>
    public static async Task<Guid> StartScanAsync(this HttpClient client, string url, byte[]? photo = null)
    {
        var response = await client.PostPhotoAsync(url, photo);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("scanId").GetGuid();
    }

    /// <summary>Polls every 20 ms until the scan is no longer Pending (at most 20 s).</summary>
    public static async Task<JsonElement> PollUntilDoneAsync(this HttpClient client, string url)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (body.GetProperty("status").GetString() != "Pending" || DateTime.UtcNow > deadline)
            {
                return body;
            }

            await Task.Delay(20);
        }
    }

    public static async Task<HttpClient> SecondOwnerAsync(this AuthApiFactory api) =>
        await api.LoginAsync((await api.AddAccountAsync(AcsTestKit.NewCode(), AcsTestKit.Password)).Username);
}
