using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// Common rule "Stale RowVersion → 409 MSG06" on LAB-04: two managers editing the same saved attendance record from the same
/// sheet must not silently overwrite each other. Every sheet row carries its record's rowVersion; a row that changes a saved
/// record without one is MSG01, team rows send <c>memberRowVersions</c> (see LaborAttendanceRowVersionTests).
/// </summary>
public class LaborAttendanceStaleEditTests
{
    [SqlServerFact]
    public async Task Editing_a_saved_attendance_record_from_an_old_sheet_is_MSG06()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var worker = new SeasonalWorker { FullName = $"Hà Văn {NewCode()}", PhoneNumber = NewPhone() };
        await ChangeAsync(api, db => db.SeasonalWorkers.Add(worker));
        var sheet = $"/api/zones/{zone.Id}/attendance/2026-08-05";
        var saved = await owner.PutAsJsonAsync(sheet, new { rows = new[] { new { workerId = worker.Id, result = "Present" } } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var read = JsonDocument.Parse(await (await owner.GetAsync($"/api/zones/{zone.Id}/attendance?date=2026-08-05")).Content.ReadAsStringAsync()).RootElement;
        var rowVersion = read.GetProperty("rows")[0].GetProperty("rowVersion").GetString();

        var withoutVersion = await owner.PutAsJsonAsync(sheet, new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Ốm" } } });
        var first = await owner.PutAsJsonAsync(sheet, new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Ốm", rowVersion } } });
        var stale = await owner.PutAsJsonAsync(sheet, new { rows = new[] { new { workerId = worker.Id, result = "UnpaidLeave", reason = "Vắng", rowVersion } } });

        Assert.Equal(HttpStatusCode.BadRequest, withoutVersion.StatusCode);
        Assert.Equal("MSG01", await withoutVersion.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal(AttendanceResult.PaidLeave, await QueryAsync(api, db => db.AttendanceRecords.Where(r => r.WorkerId == worker.Id).Select(r => r.Result).SingleAsync()));
    }
}
