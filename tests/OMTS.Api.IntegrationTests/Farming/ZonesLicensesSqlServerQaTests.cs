using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of OMTS-217 PR 0.8 on SQL Server: the proposed code taken twice, license writes racing a discontinue, Replaced at
/// UTC+7 midnight, reminder runs in parallel, the longest values in a notice and the demo seed followed by the job.
/// </summary>
public class ZonesLicensesSqlServerQaTests
{
    // GBR-03 on real SQL: discontinued codes stay reserved, case is ignored, and two screens holding one proposal cannot both save it.
    [SqlServerFact]
    public async Task The_proposal_runs_on_sql_server_and_one_proposal_saved_in_parallel_makes_one_zone()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        Assert.Equal("KV01", await NextCodeAsync(owner));
        await owner.CreateZoneAsync(NewZoneBody("KV01"));
        var old = await owner.CreateZoneAsync(NewZoneBody("kv07"));
        await (await owner.PostAsJsonAsync($"/api/zones/{old.Id()}/discontinue", new { rowVersion = old.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await owner.CreateZoneAsync(NewZoneBody("KV-NORTH"));
        await owner.CreateZoneAsync(NewZoneBody("XKV99"));

        var proposal = await NextCodeAsync(owner);
        Assert.Equal("KV08", proposal);
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostAsJsonAsync("/api/zones", NewZoneBody(proposal))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal((HttpStatusCode.Conflict, "MSG-GEN-02"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        }

        Assert.Equal("KV09", await NextCodeAsync(owner));
    }

    // FARM-02.0.E1: a loser stopped by the unique index gets what the pre-check answers: the zone named and a way to open it.
    [SqlServerFact]
    public async Task A_duplicate_stopped_by_the_unique_index_still_names_the_zone_and_offers_to_open_it()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var existing = await host.Owner.Client.CreateZoneAsync(NewZoneBody("KV05"));
        await using var racing = host.Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IFarmingZoneRepository));
            services.Remove(real);
            services.AddScoped(provider => ZoneCodeCheckPassesProxy.Wrap((IFarmingZoneRepository)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!)));
        }));
        var client = racing.CreateClient();
        var tokens = await LoginTokensAsync(client, host.Owner.Account.Username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var problem = await (await client.PostAsJsonAsync("/api/zones", NewZoneBody("KV05"))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-GEN-02", problem.Text("code"));
        Assert.StartsWith("Farming zone already exists", problem.Text("detail"));
        Assert.Equal(existing.Id(), problem.GetProperty("existingId").GetGuid());

        // An edit to the code of another zone, stopped the same way.
        var other = await host.Owner.Client.CreateZoneAsync(NewZoneBody("KV06"));
        var edit = await (await client.PutAsJsonAsync($"/api/zones/{other.Id()}", NewZoneBody("KV05") with { RowVersion = other.Text("rowVersion") })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-02", existing.Id()), (edit.Text("code"), edit.GetProperty("existingId").GetGuid()));
    }

    // GBR-01: a write and a discontinue from one version of the zone: exactly one wins, in any order, and the loser leaves no file.
    [SqlServerFact]
    public async Task A_license_create_edit_or_replace_racing_a_discontinue_lets_exactly_one_win()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < 12; round++)
        {
            var zone = await owner.CreateZoneAsync();
            var fields = new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) };
            var license = await owner.CreateLicenseAsync(fields);
            var version = (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}")).Text("rowVersion");

            var write = (round % 3) switch
            {
                0 => owner.PostAsync("/api/zone-licenses", (fields with { DocumentNumber = $"GP-{NewCode()}" }).ToForm()),
                1 => owner.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (fields with { ZoneId = null, DocumentNumber = $"GP-{NewCode()}" }).ToForm()),
                _ => owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneId = null, Holder = "Raced", RowVersion = license.Text("rowVersion") }).ToForm()),
            };
            var discontinue = owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = version });
            await Task.WhenAll(write, discontinue);

            Assert.True(
                write.Result.IsSuccessStatusCode != discontinue.Result.IsSuccessStatusCode,
                $"round {round}: write {(int)write.Result.StatusCode} {await write.Result.ErrorOrEmptyAsync()}, discontinue {(int)discontinue.Result.StatusCode} {await discontinue.Result.ErrorOrEmptyAsync()}");
            var loser = write.Result.IsSuccessStatusCode ? discontinue.Result : write.Result;
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            Assert.Contains(await loser.ErrorCodeAsync(), new[] { "MSG06", "MSG-GEN-09" });

            var (status, rows, holder) = await host.QueryAsync(async db => (
                (await db.FarmingZones.SingleAsync(z => z.Id == zone.Id())).StatusId,
                await db.ZoneComplianceDocuments.CountAsync(d => d.ZoneId == zone.Id()),
                (await db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id())).Holder));
            Assert.Equal(write.Result.IsSuccessStatusCode ? StatusIds.Usage.InUse : StatusIds.Usage.Discontinued, status);
            Assert.Equal(write.Result.IsSuccessStatusCode && round % 3 != 2 ? 2 : 1, rows);
            Assert.Equal(write.Result.IsSuccessStatusCode && round % 3 == 2 ? "Raced" : null, holder);
            await AssertFilesMatchTheDatabaseAsync(host);
        }
    }

    // The zone row serializes license writes: an edit and a replace of one license, or many adds to one zone, answer 2xx or 409, never 500.
    [SqlServerFact]
    public async Task Parallel_license_writes_in_one_zone_never_fail_with_a_server_error_or_leave_a_file()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < 4; round++)
        {
            var zone = await owner.CreateZoneAsync();
            var fields = new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) };
            var license = await owner.CreateLicenseAsync(fields);

            var responses = await Task.WhenAll(
                owner.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneId = null, Holder = "Raced", RowVersion = license.Text("rowVersion") }).ToForm()),
                owner.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (fields with { ZoneId = null }).ToForm()),
                owner.PostAsync("/api/zone-licenses", fields.ToForm()),
                owner.PostAsync("/api/zone-licenses", fields.ToForm()),
                owner.PostAsync("/api/zone-licenses", fields.ToForm()));

            foreach (var response in responses)
            {
                Assert.True((int)response.StatusCode is 200 or 201 or 409, $"round {round}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            await AssertFilesMatchTheDatabaseAsync(host);
        }
    }

    // BR-FARM-06: Replaced starts on the business date the replacement was recorded, cut at midnight UTC+7, not UTC.
    [SqlServerFact]
    public async Task Replaced_starts_at_midnight_utc_plus_7_in_the_list_and_the_detail()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zone = await owner.CreateZoneAsync();
        var old = await owner.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-2), ExpiryDate = Today.AddDays(300) });
        var renewal = await (await owner.PostAsync($"/api/zone-licenses/{old.Id()}/replace", new LicenseFields
        {
            EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddYears(3),
        }.ToForm())).ReadAsync(HttpStatusCode.Created);
        var day = Today.AddDays(-10);

        // 00:00:00 on the day, UTC+7, stored in UTC as the stamp does (17:00 of the day before).
        await RecordedAtAsync(host, renewal.Id(), new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset).ToUniversalTime());
        Assert.Equal("Valid", await StatesAsync(day.AddDays(-1)));
        Assert.Equal("Replaced", await StatesAsync(day));

        // One second earlier it is still the day before in Vietnam, although UTC says the same date as above.
        await RecordedAtAsync(host, renewal.Id(), new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset).AddSeconds(-1).ToUniversalTime());
        Assert.Equal("Replaced", await StatesAsync(day.AddDays(-1)));
        Assert.Equal("Valid", await StatesAsync(day.AddDays(-2)));

        async Task<string> StatesAsync(DateOnly asOf)
        {
            var list = (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}/licenses?asOf={asOf:yyyy-MM-dd}")).GetProperty("licenses").EnumerateArray()
                .Single(r => r.GetProperty("documentId").GetGuid() == old.Id()).Text("state");
            var detail = (await owner.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{old.Id()}?asOf={asOf:yyyy-MM-dd}")).Text("state");
            Assert.Equal(list, detail);
            return list;
        }
    }

    // Two hosts running the daily check at once (or a catch-up meeting the 07:00 run): each notice once, pushed once.
    [SqlServerFact]
    public async Task Reminder_runs_in_parallel_send_each_notice_once_and_push_it_once()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var records = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var zone = await owner.CreateZoneAsync();
            records.Add((await owner.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(5 + i) })).Id());
            records.Add((await owner.CreateCertificationAsync(new CertificationFields { ExpiryDate = Today.AddDays(5 + i) })).Id());
        }

        var job = host.Api.Services.GetRequiredService<ZoneLicenseReminderJob>();
        var sent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => job.RunOnceAsync(Today))));

        Assert.Equal(records.Count, sent.Sum());
        var told = await host.QueryAsync(db => db.Notifications.Select(n => n.RelatedRecordId!.Value).ToListAsync());
        Assert.Equal(records.Order(), told.Order());
        Assert.Equal(records.Count, host.Api.Services.GetRequiredService<FakePushSender>().Inbox.List(host.Owner.Account.Id.ToString()).Count);
    }

    // The longest values fit the notification columns; the parameters stay whole (stored JSON limit 2000).
    [SqlServerFact]
    public async Task The_longest_license_and_certification_values_fit_a_notice()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zone = await owner.CreateZoneAsync(NewZoneBody(new string('Đ', 50)));
        var license = await owner.CreateLicenseAsync(new LicenseFields
        {
            ZoneId = zone.Id(), DocumentNumber = new string('ố', 100), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(5),
        });
        var name = new string('ố', 200);
        var unnumbered = await owner.CreateCertificationAsync(new CertificationFields { Name = name, Number = null, ExpiryDate = Today.AddDays(5) });

        Assert.Equal(2, await host.Api.Services.GetRequiredService<ZoneLicenseReminderJob>().RunOnceAsync(Today));

        var notices = await host.QueryAsync(db => db.Notifications.ToDictionaryAsync(n => n.RelatedRecordId!.Value));
        Assert.Equal(new string('ố', 100), notices[license.Id()].RelatedRecordCode);
        Assert.Equal(new string('ố', 100), notices[unnumbered.Id()].RelatedRecordCode);
        Assert.Equal(name, JsonDocument.Parse(notices[unnumbered.Id()].MessageParams!).RootElement.GetProperty("certification_name").GetString());
        Assert.Equal(new string('Đ', 50), JsonDocument.Parse(notices[license.Id()].MessageParams!).RootElement.GetProperty("zone_codes")[0].GetString());
    }

    // The demo data behaves as its comments say on the first daily check after a rerun of the seed: three notices, then none.
    [SqlServerFact]
    public async Task After_a_rerun_of_the_demo_seed_the_job_reminds_only_the_three_records_meant_for_it()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
                [BootstrapSettings.OwnerDisplayNameKey] = "Nguyễn Văn Hải",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = NewPhone(),
                [BootstrapSettings.OwnerTemporaryPasswordKey] = NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            });
            for (var run = 0; run < 2; run++)
            {
                using var scope = api.Services.CreateScope();
                Assert.Equal(DeploymentBootstrapper.Success, (await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(seedDemoData: true, CancellationToken.None)).ExitCode);
            }

            var job = api.Services.GetRequiredService<ZoneLicenseReminderJob>();
            Assert.Equal(3, await job.RunOnceAsync(Today));
            Assert.Equal(0, await job.RunOnceAsync(Today));

            using var read = api.Services.CreateScope();
            var db = read.ServiceProvider.GetRequiredService<OMTS.Infrastructure.Persistence.OmtsDbContext>();
            var codes = await db.Notifications
                .Where(n => n.Type == OMTS.Application.Features.Notifications.NotificationTypes.ZoneLicenseExpiry
                    || n.Type == OMTS.Application.Features.Notifications.NotificationTypes.CooperativeCertificationExpiry)
                .Select(n => n.RelatedRecordCode).Distinct().ToListAsync();
            Assert.Equal(new[] { "ATTP-DEMO-02", "ATTP-DEMO-2024-02", "NTTS-2025-DEMO-01" }, codes.Order());
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task AssertFilesMatchTheDatabaseAsync(FarmingSqlServerHost host)
    {
        var keys = await host.QueryAsync(db => db.ZoneComplianceDocuments.Select(d => d.EvidenceFileUrl!).ToListAsync());
        Assert.Equal(keys.Order(), host.StoredKeys("zone-licenses"));
    }

    // Creation stamps never change through the context, so the recorded time is set directly.
    private static Task RecordedAtAsync(FarmingSqlServerHost host, Guid licenseId, DateTimeOffset recorded) => host.QueryAsync(db =>
        db.Database.ExecuteSqlAsync($"UPDATE ZoneComplianceDocuments SET CreatedAt = {recorded} WHERE Id = {licenseId}"));

    private static async Task<string> NextCodeAsync(HttpClient owner) =>
        (await (await owner.GetAsync("/api/zones/next-code")).ReadAsync(HttpStatusCode.OK)).Text("code");
}

/// <summary>
/// The zone repository of one request whose first code check finds nothing, as when a parallel create has not committed
/// yet; a later look-up, after the unique index stopped the save, reaches the database.
/// </summary>
public class ZoneCodeCheckPassesProxy : DispatchProxy
{
    private IFarmingZoneRepository _inner = null!;
    private bool _checked;

    public static IFarmingZoneRepository Wrap(IFarmingZoneRepository inner)
    {
        var proxy = Create<IFarmingZoneRepository, ZoneCodeCheckPassesProxy>();
        ((ZoneCodeCheckPassesProxy)(object)proxy)._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IFarmingZoneRepository.FindByCodeAsync) && !_checked)
        {
            _checked = true;
            return Task.FromResult<FarmingZone?>(null);
        }

        return targetMethod.Invoke(_inner, args);
    }
}

internal static class ZonesLicensesSqlServerQaKit
{
    /// <summary>The answer's body (buffered, so it can be read again), for a failure message.</summary>
    public static async Task<string> ErrorOrEmptyAsync(this HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        return await response.Content.ReadAsStringAsync();
    }
}
