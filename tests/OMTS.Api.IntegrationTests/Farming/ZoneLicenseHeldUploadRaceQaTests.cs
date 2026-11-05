using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of OMTS-217 PR 0.8 on SQL Server: the races of FARM-03 made certain instead of likely. A license write reads the zone,
/// uploads its file and then saves; here the upload is held while the other request commits, so the write always saves with
/// a zone it read before that commit.
/// </summary>
public class ZoneLicenseHeldUploadRaceQaTests
{
    // GBR-01: the zone was discontinued after the write read it: the write fails with MSG06 and its upload is removed.
    [SqlServerFact]
    public async Task A_discontinue_committed_during_a_license_write_stops_the_write_and_removes_its_file()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var hold = new UploadHold();
        await using var held = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(hold.Install));
        var owner = await LoginAsync(held.CreateClient(), host);

        for (var kind = 0; kind < 3; kind++)
        {
            var zone = await owner.CreateZoneAsync();
            var fields = new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) };
            var license = await owner.CreateLicenseAsync(fields);
            var version = (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}")).Text("rowVersion");

            hold.Arm();
            var write = kind switch
            {
                0 => owner.PostAsync("/api/zone-licenses", (fields with { DocumentNumber = $"GP-{NewCode()}" }).ToForm()),
                1 => owner.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (fields with { ZoneId = null, DocumentNumber = $"GP-{NewCode()}" }).ToForm()),
                _ => owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneId = null, Holder = "Too late", RowVersion = license.Text("rowVersion") }).ToForm()),
            };
            await hold.EnteredAsync();
            await (await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = version })).ReadAsync(HttpStatusCode.OK);
            hold.Release();

            var refused = await write;
            Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (refused.StatusCode, await refused.ErrorCodeAsync()));
            var (status, rows, holder) = await host.QueryAsync(async db => (
                (await db.FarmingZones.SingleAsync(z => z.Id == zone.Id())).StatusId,
                await db.ZoneComplianceDocuments.CountAsync(d => d.ZoneId == zone.Id()),
                (await db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id())).Holder));
            Assert.Equal((StatusIds.Usage.Discontinued, 1, (string?)null), (status, rows, holder));
            await AssertFilesMatchTheDatabaseAsync(host);
        }
    }

    // A Replaced license is kept as it is: an edit that read it before the replacement committed cannot change it.
    [SqlServerFact]
    public async Task An_edit_cannot_land_on_a_license_replaced_while_it_was_uploading()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var hold = new UploadHold();
        await using var held = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(hold.Install));
        var owner = await LoginAsync(held.CreateClient(), host);
        var zone = await owner.CreateZoneAsync();
        var fields = new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) };
        var license = await owner.CreateLicenseAsync(fields);

        hold.Arm();
        var edit = owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneId = null, Holder = "Too late", RowVersion = license.Text("rowVersion") }).ToForm());
        await hold.EnteredAsync();
        await (await owner.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (fields with { ZoneId = null, DocumentNumber = $"GP-{NewCode()}" }).ToForm()))
            .ReadAsync(HttpStatusCode.Created);
        hold.Release();

        var refused = await edit;
        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        Assert.Null((await host.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()))).Holder);
        await AssertFilesMatchTheDatabaseAsync(host);
    }

    // A replace that read the license before an edit committed fails too: it would link to data it never saw.
    [SqlServerFact]
    public async Task A_replace_is_stopped_by_an_edit_committed_while_it_was_uploading()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var hold = new UploadHold();
        await using var held = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(hold.Install));
        var owner = await LoginAsync(held.CreateClient(), host);
        var zone = await owner.CreateZoneAsync();
        var fields = new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) };
        var license = await owner.CreateLicenseAsync(fields);

        hold.Arm();
        var replace = owner.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (fields with { ZoneId = null, DocumentNumber = $"GP-{NewCode()}" }).ToForm());
        await hold.EnteredAsync();
        await (await owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneId = null, Holder = "Corrected", File = null, RowVersion = license.Text("rowVersion") }).ToForm()))
            .ReadAsync(HttpStatusCode.OK);
        hold.Release();

        var refused = await replace;
        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        Assert.Equal(0, await host.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.ReplacesDocumentId == license.Id())));
        await AssertFilesMatchTheDatabaseAsync(host);
    }

    private static async Task<HttpClient> LoginAsync(HttpClient client, FarmingSqlServerHost host)
    {
        var tokens = await LoginTokensAsync(client, host.Owner.Account.Username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task AssertFilesMatchTheDatabaseAsync(FarmingSqlServerHost host)
    {
        var keys = await host.QueryAsync(db => db.ZoneComplianceDocuments.Select(d => d.EvidenceFileUrl!).ToListAsync());
        Assert.Equal(keys.Order(), host.StoredKeys("zone-licenses"));
    }

    /// <summary>Holds the first upload after <see cref="Arm"/> until <see cref="Release"/>; later uploads pass.</summary>
    private sealed class UploadHold
    {
        private int _armed;
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualResetEventSlim _released = new(false);

        public void Install(IServiceCollection services)
        {
            var real = services.Last(d => d.ServiceType == typeof(IFileStorage));
            services.RemoveAll<IFileStorage>();
            services.AddSingleton(provider => UploadHoldingStorageProxy.Wrap((IFileStorage)real.ImplementationFactory!(provider), BeforeUpload));
        }

        public void Arm()
        {
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _released = new ManualResetEventSlim(false);
            Volatile.Write(ref _armed, 1);
        }

        public Task EnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Release() => _released.Set();

        private void BeforeUpload()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _entered.TrySetResult();
                _released.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }
}

/// <summary>The real storage with a callback before each upload.</summary>
public class UploadHoldingStorageProxy : DispatchProxy
{
    private IFileStorage _inner = null!;
    private Action _beforeUpload = null!;

    public static IFileStorage Wrap(IFileStorage inner, Action beforeUpload)
    {
        var storage = Create<IFileStorage, UploadHoldingStorageProxy>();
        var proxy = (UploadHoldingStorageProxy)(object)storage;
        (proxy._inner, proxy._beforeUpload) = (inner, beforeUpload);
        return storage;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IFileStorage.UploadAsync))
        {
            _beforeUpload();
        }

        return targetMethod.Invoke(_inner, args);
    }
}
