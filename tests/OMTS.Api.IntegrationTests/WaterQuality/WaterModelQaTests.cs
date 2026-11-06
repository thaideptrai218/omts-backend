using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of the water model against SRS 27/10: AC-04 in full, the alert (BR-WATER-04, BR-WATER-05), the raft rule
/// (BR-WATER-01), access per §I.4.4 and the measured time (BR-WATER-03). A skipped test names its finding and says why it
/// stays open (see reports/qa-261104-water-model.md).
/// </summary>
public class WaterModelQaTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    // AC-04 in one run: 21 read, 12 confirmed, alert, a later change of the thresholds, and a correction against the copy.
    [Fact]
    public async Task Ac_04_nothing_before_the_confirmation_the_alert_on_12_and_a_threshold_change_keeps_every_bound_of_the_copy()
    {
        var z = await api.ZoneAsync();
        var (manager, managerAccount) = await api.StaffAsync(z.Zone);

        var recognized = await (await z.RecognizeAsync("ph7.8_sal21_tmp28.5", manager)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(21m, recognized.GetProperty("ocr").GetProperty("salinity").GetProperty("value").GetDecimal());
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.ZoneId == z.Zone.Id && e.Action.StartsWith("WaterMeasurement."))));
        Assert.Equal(0, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == managerAccount.Id && n.MessageCode == "MSG-WATER-03")));

        var saved = await z.RecordAsync(7.8m, 12m, 28.5m, client: manager, photoKey: recognized.Text("photoKey"));
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal(12m, body.GetProperty("salinity").GetDecimal());
        var alerts = await api.AlertsOfAsync(body.Id());
        Assert.Single(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == managerAccount.Id);
        Assert.All(alerts, n => Assert.Contains("Salinity 12 ‰ is too low (allowed 15–30 ‰)", n.Body));

        var changed = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new
        {
            phMin = 6m, phMax = 9m, salinityMin = 10m, salinityMax = 31m, temperatureMin = 5m, temperatureMax = 40m,
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var detail = await (await z.Owner.GetAsync($"/api/water-measurements/{body.Id()}")).Content.ReadFromJsonAsync<JsonElement>();
        var copy = detail.GetProperty("thresholdsUsed");
        Assert.Equal(
            (6.5m, 8.5m, 15m, 30m, 10m, 35m),
            (copy.GetProperty("phMin").GetDecimal(), copy.GetProperty("phMax").GetDecimal(), copy.GetProperty("salinityMin").GetDecimal(),
                copy.GetProperty("salinityMax").GetDecimal(), copy.GetProperty("temperatureMin").GetDecimal(), copy.GetProperty("temperatureMax").GetDecimal()));
        Assert.Equal(("Active", "Salinity 12.00 < 15.00"), (detail.Text("alertState"), detail.Text("violatedValues")));

        // A new measurement of 12 is inside the new thresholds and alerts nobody.
        var next = await z.RecordedAsync(7.8m, 12m, client: manager);
        Assert.Equal(10m, next.GetProperty("thresholdsUsed").GetProperty("salinityMin").GetDecimal());
        Assert.Empty(await api.AlertsOfAsync(next.Id()));

        // The temperature is checked against the copy too: 36 is inside today's 5–40, outside the 10–35 of the measurement.
        var corrected = await (await manager.CorrectAsync(body.Id(), 7.8m, 20m, temperature: 36m)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Temperature 36.00 > 35.00", corrected.Text("violatedValues"));
    }

    [Theory]
    [InlineData(6.5, 15, 10, null)]
    [InlineData(8.5, 30, 35, null)]
    [InlineData(6.49, 20, 25, "pH 6.49 < 6.50")]
    [InlineData(8.51, 20, 25, "pH 8.51 > 8.50")]
    [InlineData(7, 14.99, 25, "Salinity 14.99 < 15.00")]
    [InlineData(7, 30.01, 25, "Salinity 30.01 > 30.00")]
    [InlineData(7, 20, 9.99, "Temperature 9.99 < 10.00")]
    [InlineData(7, 20, 35.01, "Temperature 35.01 > 35.00")]
    public async Task A_bound_is_inside_and_one_hundredth_past_it_alerts(double ph, double salinity, double temperature, string? violated)
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);

        var body = await z.RecordedAsync((decimal)ph, (decimal)salinity, client: manager, temperature: (decimal)temperature);

        Assert.Equal(violated, body.Text("violatedValues"));
        Assert.Equal(violated is not null, body.GetProperty("thresholdExceeded").GetBoolean());
        Assert.Equal(violated is null ? "None" : "Active", body.Text("alertState"));
        Assert.Equal(violated is not null, (await api.AlertsOfAsync(body.Id())).Count > 0);
    }

    [Fact]
    public async Task An_indicator_without_thresholds_is_never_checked_and_a_zone_without_any_has_no_alert()
    {
        var none = await api.ZoneAsync(thresholds: false);
        var extreme = await none.RecordedAsync(14m, 50m, temperature: 45m);
        Assert.Equal((false, "None"), (extreme.GetProperty("thresholdExceeded").GetBoolean(), extreme.Text("alertState")));
        Assert.Empty(await api.AlertsOfAsync(extreme.Id()));

        var z = await api.ZoneAsync(thresholds: false);
        var set = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { temperatureMin = 10m, temperatureMax = 35m });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var (manager, _) = await api.StaffAsync(z.Zone);

        var inside = await z.RecordedAsync(14m, 50m, client: manager, temperature: 35m);
        var outside = await z.RecordedAsync(0m, 0m, client: manager, temperature: 35.5m);

        Assert.Null(inside.Text("violatedValues"));
        Assert.Equal("Temperature 35.50 > 35.00", outside.Text("violatedValues"));
        Assert.All(await api.AlertsOfAsync(outside.Id()), n => Assert.Contains("Temperature 35.5 °C is too high (allowed 10–35 °C)", n.Body));
    }

    // BR-WATER-05: the Owner and every Zone Manager with access; a locked account, a revoked pair, another zone and the Accountant are out.
    [Fact]
    public async Task The_alert_reaches_exactly_the_owner_and_the_zone_managers_with_effective_access_and_push_cannot_be_turned_off()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var (recorder, recorderAccount) = await api.StaffAsync(z.Zone);
        var (_, pushOff) = await api.StaffAsync(z.Zone);
        await api.UpdateAsync<Account>(pushOff.Id, a => a.NotificationPreferences.Add(new NotificationPreference { Type = "WATER_ALERT", Push = false }));
        var (_, locked) = await api.StaffAsync(z.Zone);
        await api.UpdateAsync<Account>(locked.Id, a => a.StatusId = StatusIds.Account.Locked);
        var (_, revoked) = await api.StaffAsync(z.Zone);
        var revokedGrant = await api.QueryAsync(db => db.AccessGrants.SingleAsync(g => g.AccountId == revoked.Id));
        await api.UpdateAsync<AccessGrant>(revokedGrant.Id, g => g.StatusId = StatusIds.AccessGrant.Revoked);
        var (_, elsewhere) = await api.StaffAsync(other.Zone);
        var (_, accountantHere) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var bothRoles = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        await api.GrantAsync(bothRoles, StaffRole.Accountant, z.Zone);
        await api.GrantAsync(bothRoles, StaffRole.ZoneManager, other.Zone);

        var body = await z.RecordedAsync(7m, 20m, client: recorder, temperature: 40m);

        var alerts = await api.AlertsOfAsync(body.Id());
        foreach (var expected in new[] { z.OwnerAccount.Id, recorderAccount.Id, pushOff.Id })
        {
            Assert.Single(alerts, n => n.RecipientAccountId == expected);
        }

        foreach (var left in new[] { locked.Id, revoked.Id, elsewhere.Id, accountantHere.Id, bothRoles.Id })
        {
            Assert.DoesNotContain(alerts, n => n.RecipientAccountId == left);
        }

        Assert.All(alerts, n => Assert.True(n.IsMandatory));
        var pushed = api.Services.GetRequiredService<FakePushSender>().Inbox.List(pushOff.Id.ToString());
        Assert.Contains(pushed, m => m.Text.Contains("Temperature 40 °C is too high"));
    }

    [Fact]
    public async Task The_parameters_of_the_alert_are_atomic_and_name_the_raft_of_the_measurement()
    {
        var z = await api.ZoneAsync();
        var second = await api.RaftAsync(z.Zone);
        var (manager, managerAccount) = await api.StaffAsync(z.Zone);
        var measuredAt = new DateTimeOffset(2026, 11, 1, 8, 15, 0, TimeSpan.FromHours(7));

        var saved = await z.RecordAsync(7m, 20m, 9.25m, client: manager, raftId: second.Id, measuredAt: measuredAt);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var alert = Assert.Single(await api.AlertsOfAsync(body.Id()), n => n.RecipientAccountId == managerAccount.Id);
        var parameters = JsonDocument.Parse(alert.MessageParams!).RootElement;

        Assert.Equal(second.Code, parameters.GetProperty("raft_code").GetString());
        Assert.Equal(z.Zone.Name, parameters.GetProperty("zone_name").GetString());
        Assert.False(parameters.GetProperty("corrected").GetBoolean());
        var time = parameters.GetProperty("measurement_time").GetDateTimeOffset();
        Assert.Equal((measuredAt, BusinessClock.UtcOffset), (time, time.Offset));
        var value = Assert.Single(parameters.GetProperty("values_outside").EnumerateArray());
        Assert.Equal(
            ("TEMPERATURE", 9.25m, "°C", "Low", 10m, 35m),
            (value.GetProperty("indicator").GetString(), value.GetProperty("value").GetDecimal(), value.GetProperty("unit").GetString(),
                value.GetProperty("direction").GetString(), value.GetProperty("min").GetDecimal(), value.GetProperty("max").GetDecimal()));
        Assert.Contains($"raft {second.Code}, measured on {BusinessClock.Format(measuredAt)}.", alert.Body);
    }

    // BR-WATER-01 and GBR-01 are checked when saving, whatever was true at the recognition; a refusal does not use up the photo.
    [Fact]
    public async Task A_raft_or_zone_discontinued_after_the_recognition_is_refused_and_the_photo_can_still_be_used()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var spare = await api.RaftAsync(z.Zone);
        var key = await manager.PhotoKeyAsync(z.Zone.Id, "ph7.2_sal20_tmp25");
        await api.UpdateAsync<Raft>(z.Raft.Id, r => r.StatusId = StatusIds.Usage.Discontinued);

        var refused = await z.RecordAsync(7.2m, 20m, client: manager, photoKey: key);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("Raft", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));

        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);
        var zoneRefused = await z.RecordAsync(7.2m, 20m, client: manager, raftId: spare.Id, photoKey: key);
        Assert.Equal("Zone", (await zoneRefused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));

        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.InUse);
        var saved = await z.RecordAsync(7.2m, 20m, client: manager, raftId: spare.Id, photoKey: key);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal(spare.Code, (await saved.Content.ReadFromJsonAsync<JsonElement>()).Text("raftCode"));
    }

    // WATER-01 step 3: the raft list the client is told to use is open to the Zone Manager of the zone and closed to the others.
    [Fact]
    public async Task The_rafts_in_use_of_the_zone_are_listed_for_its_zone_manager_only()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var (otherManager, _) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        var discontinued = await api.RaftAsync(z.Zone);
        await api.UpdateAsync<Raft>(discontinued.Id, r => r.StatusId = StatusIds.Usage.Discontinued);
        var url = $"/api/zones/{z.Zone.Id}/rafts?statusId={StatusIds.Usage.InUse}";

        var listed = await manager.GetAsync(url);
        var text = await listed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Contains(z.Raft.Code, text);
        Assert.DoesNotContain(discontinued.Code, text);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherManager.GetAsync(url)).StatusCode);
    }

    // §I.4.4: WATER-01…03 Owner Full, Zone Manager Full in the zone, others No; WATER-04 Owner only.
    [Fact]
    public async Task Every_water_route_follows_the_matrix_for_each_role()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        // Recorded by the manager: WATER-02 limits a Zone Manager to their own measurements.
        var id = (await z.RecordedAsync(7m, 20m, client: manager)).Id();
        var (otherManager, _) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        var (accountant, _) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var accountantAllPassword = NewPassword();
        var accountantAllAccount = await api.AddAccountAsync(NewCode(), accountantAllPassword, SubjectType.Staff);
        await api.GrantAsync(accountantAllAccount, StaffRole.Accountant, zone: null);
        var accountantAll = await api.LoginAsync(accountantAllAccount.Username, accountantAllPassword);
        var thresholds = new { phMin = 6.5m, phMax = 8.5m, salinityMin = 15m, salinityMax = 30m, temperatureMin = 10m, temperatureMax = 35m };
        var routes = new (string Name, bool OwnerOnly, Func<HttpClient, Task<HttpResponseMessage>> Call)[]
        {
            ("recognize", false, c => c.RecognizeAsync(z.Zone.Id)),
            ("record", false, c => c.PostAsJsonAsync(z.Base, new { photoKey = "none" })),
            ("history", false, c => c.GetAsync(z.Base)),
            ("chart", false, c => c.GetAsync($"{z.Base}/chart")),
            ("export", false, c => c.GetAsync($"{z.Base}/export")),
            ("detail", false, c => c.GetAsync($"/api/water-measurements/{id}")),
            ("correct", false, c => c.CorrectAsync(id, 7.1m, 20m)),
            ("zone thresholds", false, c => c.GetAsync($"/api/zones/{z.Zone.Id}/water-thresholds")),
            ("set zone thresholds", true, c => c.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", thresholds)),
            ("set all thresholds", true, c => c.PutAsJsonAsync("/api/water-thresholds", thresholds)),
            ("void", false, c => c.VoidAsync(id)),
        };

        var wrong = new List<string>();
        foreach (var (name, ownerOnly, call) in routes)
        {
            Expect(name, "anonymous", (await call(api.CreateClient())).StatusCode, HttpStatusCode.Unauthorized);
            Expect(name, "manager of another zone", (await call(otherManager)).StatusCode, HttpStatusCode.Forbidden);
            Expect(name, "accountant of the zone", (await call(accountant)).StatusCode, HttpStatusCode.Forbidden);
            Expect(name, "accountant at all zones", (await call(accountantAll)).StatusCode, HttpStatusCode.Forbidden);
            var managerAnswer = (await call(manager)).StatusCode;
            if (ownerOnly)
            {
                Expect(name, "manager of the zone", managerAnswer, HttpStatusCode.Forbidden);
            }
            else if (managerAnswer is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                wrong.Add($"{name}: manager of the zone got {(int)managerAnswer}");
            }

            var ownerAnswer = (await call(z.Owner)).StatusCode;
            if (ownerAnswer is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                wrong.Add($"{name}: owner got {(int)ownerAnswer}");
            }
        }

        // The list of thresholds is a read of the zones the caller can view: an empty answer outside the matrix, none of this zone.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/water-thresholds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/water-thresholds")).StatusCode);
        Assert.DoesNotContain(z.Zone.Code, await (await otherManager.GetAsync("/api/water-thresholds")).Content.ReadAsStringAsync());
        Assert.Contains(z.Zone.Code, await (await manager.GetAsync("/api/water-thresholds")).Content.ReadAsStringAsync());
        Assert.Empty(wrong);

        void Expect(string route, string who, HttpStatusCode actual, HttpStatusCode expected)
        {
            if (actual != expected)
            {
                wrong.Add($"{route}: {who} got {(int)actual}, expected {(int)expected}");
            }
        }
    }

    // The photo goes out only through the detail, after the zone check; its link is signed for that one key.
    [Fact]
    public async Task The_photo_of_a_measurement_is_reachable_only_through_the_detail_of_a_zone_the_caller_can_view()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var (otherManager, _) = await api.StaffAsync(other.Zone);
        var mine = await z.RecordedAsync(7m, 20m, client: manager);
        var theirs = await other.RecordedAsync(7m, 20m, client: otherManager);

        var refused = await manager.GetAsync($"/api/water-measurements/{theirs.Id()}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain("photoUrl", await refused.Content.ReadAsStringAsync());
        Assert.DoesNotContain("photoUrl", await (await manager.GetAsync(z.Base)).Content.ReadAsStringAsync());

        var (myUrl, theirUrl) = (mine.Text("photoUrl")!, theirs.Text("photoUrl")!);
        var anonymous = api.CreateClient();
        var download = await anonymous.GetAsync(myUrl);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);

        var mySignature = myUrl[myUrl.IndexOf('?')..];
        var theirPath = theirUrl[..theirUrl.IndexOf('?')];
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(theirPath + mySignature)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(theirPath)).StatusCode);
    }

    // BR-WATER-03: a time without an offset is Vietnam time, whatever the server's own zone.
    [Fact]
    public async Task A_time_without_an_offset_is_read_as_vietnam_time_on_both_sides_of_now()
    {
        var z = await api.ZoneAsync();
        var vietnamNow = DateTimeOffset.UtcNow.ToOffset(BusinessClock.UtcOffset);

        var justBefore = await PostTimeAsync(z, vietnamNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss"));
        var ahead = await PostTimeAsync(z, vietnamNow.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ss"));

        Assert.Equal(HttpStatusCode.Created, justBefore.StatusCode);
        var stored = (await justBefore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measuredAt").GetDateTimeOffset();
        Assert.InRange(stored, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.BadRequest, ahead.StatusCode);
        Assert.Equal("Measured at", (await ahead.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    // FINDING F-04: BR-WATER-03 and the entity say "not later than now"; the SRS has no tolerance, the branch keeps 5 minutes
    // and stores the future time as it was sent. If the PM accepts the tolerance, drop this test; if not, it must pass.
    [Fact(Skip = "Waiting for the PM: is a 5-minute tolerance for clock differences acceptable against 'not later than now'?")]
    public async Task F04_a_measurement_time_later_than_now_is_refused()
    {
        var z = await api.ZoneAsync();

        var response = await z.RecordAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddMinutes(4));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // WATER-01 POST-1: a saved measurement is listed in WATER-03, and the history refuses a period that starts before
    // WaterPeriod.Earliest. So an earlier time, such as a typing error of the year, is refused and saves nothing.
    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("0206-11-04T08:00:00+07:00")]
    [InlineData("1900-01-01T00:00:00")]
    [InlineData("1999-12-31T08:00:00+07:00")]
    [InlineData("1999-12-31T23:59:59+07:00")]
    [InlineData("1999-12-31T23:59:59")]
    [InlineData("1999-12-31T16:59:59Z")]
    public async Task A_time_before_the_first_day_of_the_history_is_refused_on_the_time_and_saves_nothing(string measuredAt)
    {
        var z = await api.ZoneAsync();

        var refused = await PostTimeAsync(z, measuredAt);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("MSG-GEN-01", await refused.ErrorCodeAsync());
        Assert.Equal("Measured at", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    // The limit is the first business day: 17:00 UTC is already 2000-01-01 in Vietnam, and that day can be listed.
    [Theory]
    [InlineData("2000-01-01T00:00:00+07:00")]
    [InlineData("2000-01-01T00:00:00")]
    [InlineData("1999-12-31T17:00:00Z")]
    public async Task A_time_on_the_first_day_of_the_history_is_saved_and_can_be_found(string measuredAt)
    {
        var z = await api.ZoneAsync();

        var saved = await PostTimeAsync(z, measuredAt);

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var list = await z.Owner.GetAsync($"{z.Base}?from=2000-01-01&to=2000-01-01");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains((await saved.Content.ReadFromJsonAsync<JsonElement>()).Id().ToString(), await list.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_correction_to_a_time_before_the_first_day_of_the_history_is_refused()
    {
        var z = await api.ZoneAsync();
        var measuredAt = DateTimeOffset.UtcNow.AddHours(-1);
        var id = (await z.RecordedAsync(7m, 20m, measuredAt)).Id();

        var refused = await z.Owner.CorrectAsync(id, 7.1m, 20m, measuredAt: new DateTimeOffset(1999, 12, 31, 23, 59, 59, BusinessClock.UtcOffset));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("MSG-GEN-01", await refused.ErrorCodeAsync());
        Assert.Equal("Measured at", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        var kept = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id));
        Assert.Equal((7m, measuredAt), (kept.Ph, kept.MeasuredAt));
    }

    // The client list says these are ignored; a client that has not been updated must not save a Draft or fail.
    [Fact]
    public async Task Removed_fields_of_the_old_flow_are_ignored_and_the_saved_row_is_active()
    {
        var z = await api.ZoneAsync();

        var saved = await z.Owner.PostAsJsonAsync(z.Base, new
        {
            confirm = false,
            inputSource = "Manual",
            measurementPoint = "Phao 1",
            recordVersion = 7,
            raftId = z.Raft.Id,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ph = 7m,
            salinity = 20m,
            temperature = 25m,
            photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id),
        });

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE", body.Text("statusCode"));
        foreach (var removed in new[] { "measurementPoint", "inputSource", "ocrDraft", "confirmedBy", "recordVersion", "versions" })
        {
            Assert.False(body.TryGetProperty(removed, out _), removed);
        }
    }

    // Nothing in the code base may still need what was removed.
    [Fact]
    public void No_route_type_or_property_of_the_old_model_is_left()
    {
        var routes = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(r => r.Contains("water-measurements", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(routes);
        Assert.DoesNotContain(routes, r => r.EndsWith("with-photo", StringComparison.OrdinalIgnoreCase) || r.EndsWith("confirm", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, r => r.Contains("{id", StringComparison.OrdinalIgnoreCase) && r.EndsWith("recognize", StringComparison.OrdinalIgnoreCase));

        var domain = typeof(FarmingZone).Assembly;
        Assert.Null(domain.GetType("OMTS.Domain.Enums.MeasurementInputSource"));
        Assert.Null(typeof(FarmingZone).GetProperty("WaterMeasurementPoint"));
        foreach (var removed in new[] { "MeasurementPoint", "InputSource", "OcrPh", "OcrSalinity", "OcrMeasuredAt", "ConfirmedAt", "RecordVersion", "CorrectedFromMeasurementId" })
        {
            Assert.Null(typeof(OMTS.Domain.Entities.Cultivation.WaterMeasurement).GetProperty(removed));
        }
    }

    private async Task<HttpResponseMessage> PostTimeAsync(WaterZone z, string measuredAt) =>
        await z.Owner.PostAsJsonAsync(z.Base, new
        {
            raftId = z.Raft.Id,
            measuredAt,
            ph = 7m,
            salinity = 20m,
            temperature = 25m,
            photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id),
        });
}
