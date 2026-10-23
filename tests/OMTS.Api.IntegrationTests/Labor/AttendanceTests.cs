using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-04 attendance sheet: suggestions, team expansion with overrides, all-or-nothing save, uniqueness, Left, lock, reasons.</summary>
public class AttendanceTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    private static string Sheet(LaborWorld w, DateOnly date) => $"{w.Attendance}/{date:yyyy-MM-dd}";

    [Fact]
    public async Task Sheet_suggests_people_of_the_day_s_daily_wage_assignments_until_they_are_recorded()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(8);
        var team = await w.AddTeamAsync();
        var first = await w.AddWorkerAsync(team.Id);
        var second = await w.AddWorkerAsync(team.Id);
        await w.AddWorkerAsync(team.Id, x => x.StatusId = StatusIds.Usage.Discontinued);
        var employee = await w.AddEmployeeAsync();
        foreach (var body in new object[]
        {
            new { workDate = date, teamId = team.Id, task = "Kéo bè", paymentType = "DailyWage" },
            new { workDate = date, employeeId = employee.Id, task = "Trực bè", paymentType = "DailyWage" },
        })
        {
            await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, body), HttpStatusCode.Created);
        }

        var before = await ExpectAsync(await w.Owner.GetAsync($"{w.Attendance}?date={date:yyyy-MM-dd}"), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = first.Id, result = "Present" } } }), HttpStatusCode.OK);
        var after = await ExpectAsync(await w.Owner.GetAsync($"{w.Attendance}?date={date:yyyy-MM-dd}"), HttpStatusCode.OK);

        var suggested = Rows(before).Where(r => r.GetProperty("suggested").GetBoolean()).Select(r => r.GetProperty("personId").GetGuid()).ToHashSet();
        Assert.Equal(new HashSet<Guid> { first.Id, second.Id, employee.Id }, suggested);
        Assert.Contains(Rows(after), r => r.GetProperty("personId").GetGuid() == first.Id && !r.GetProperty("suggested").GetBoolean());
        Assert.Equal(2, Rows(after).Count(r => r.GetProperty("suggested").GetBoolean()));
    }

    [Fact]
    public async Task Team_row_expands_to_every_in_use_member_and_an_individual_row_overrides_it()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(9);
        var team = await w.AddTeamAsync();
        var present = await w.AddWorkerAsync(team.Id);
        var absent = await w.AddWorkerAsync(team.Id);

        var sheet = await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new
        {
            rows = new object[]
            {
                new { teamId = team.Id, result = "Present", hours = 8 },
                new { workerId = absent.Id, result = "UnpaidLeave" },
            },
        }), HttpStatusCode.OK);

        var records = await factory.QueryAsync(db => db.AttendanceRecords.AsNoTracking().Where(r => r.WorkDate == date && r.ZoneId == w.Zone.Id).ToListAsync());
        Assert.Equal(2, records.Count);
        var presentRecord = records.Single(r => r.WorkerId == present.Id);
        Assert.Equal((AttendanceResult.Present, 8m, team.Id), (presentRecord.Result, presentRecord.Hours!.Value, presentRecord.ContractorId!.Value));
        Assert.Equal(AttendanceResult.UnpaidLeave, records.Single(r => r.WorkerId == absent.Id).Result);
        Assert.Equal(2, Rows(sheet).Count(r => !r.GetProperty("suggested").GetBoolean()));
    }

    [Fact]
    public async Task One_invalid_row_saves_nothing_and_every_refused_row_is_listed()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(10);
        var valid = await w.AddWorkerAsync();
        var discontinued = await w.AddWorkerAsync(configure: x => x.StatusId = StatusIds.Usage.Discontinued);

        var problem = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), new
        {
            rows = new object[]
            {
                new { workerId = valid.Id, result = "Present" },
                new { workerId = discontinued.Id, result = "Present" },
                new { workerId = valid.Id, result = "Present", hours = 0.3 },
            },
        })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        // The problem names the first refused row; every refused row is listed with its own field.
        Assert.Equal("Worker", problem.Field());
        var refused = problem.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal([1, 2], refused.Select(r => r.GetProperty("index").GetInt32()));
        Assert.Equal(["Worker", "Hours"], refused.Select(r => r.GetProperty("field").GetString()));
        Assert.Equal(0, await factory.QueryAsync(db => db.AttendanceRecords.CountAsync(r => r.WorkDate == date && r.ZoneId == w.Zone.Id)));
    }

    [Fact]
    public async Task Discontinued_worker_alone_is_MSG01_on_the_worker()
    {
        var w = await factory.WorldAsync();
        var discontinued = await w.AddWorkerAsync(configure: x => x.StatusId = StatusIds.Usage.Discontinued);

        var problem = await (await w.Owner.PutAsJsonAsync(Sheet(w, NewMonth().Day(3)), new { rows = new[] { new { workerId = discontinued.Id, result = "Present" } } }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal("Worker", problem.Field());
    }

    [Fact]
    public async Task A_record_of_the_same_day_in_another_zone_is_MSG03()
    {
        var w = await factory.WorldAsync();
        var other = await factory.AddZoneAsync(NewCode());
        var worker = await w.AddWorkerAsync(configure: x => x.FullName = $"Ngô Văn {NewCode()}");
        var date = NewMonth().Day(11);
        await ExpectAsync(await w.Owner.PutAsJsonAsync($"/api/zones/{other.Id}/attendance/{date:yyyy-MM-dd}",
            new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);

        var problem = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG03");

        Assert.Equal($"Attendance of {worker.FullName} on {date:dd/MM/yyyy} already exists. The system opens the existing record for you to check and does not create a copy.",
            problem.GetProperty("detail").GetString());
        Assert.True(problem.TryGetProperty("rows", out _));
    }

    [Fact]
    public async Task Employee_who_left_on_or_before_the_day_is_MSG72()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var employee = await w.AddEmployeeAsync(configure: e => (e.StatusId, e.EmploymentEndDate) = (StatusIds.Employee.Left, month.Day(5)));

        var problem = await (await w.Owner.PutAsJsonAsync(Sheet(w, month.Day(6)), new { rows = new[] { new { employeeId = employee.Id, result = "Present" } } }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG72");

        Assert.Contains($"has left since 05/{month.Month:D2}/{month.Year}", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Future_date_is_MSG01()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).AddDays(1);

        var problem = await (await w.Owner.PutAsJsonAsync(Sheet(w, tomorrow), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal("Work date", problem.Field());
    }

    [Fact]
    public async Task Editing_a_saved_record_needs_a_reason_and_the_edit_right()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(12);
        var (creator, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.Attendance, true, true, false, false));
        await ExpectAsync(await creator.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);

        var noReason = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave" } } }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var noRight = await creator.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Nghỉ phép" } } });
        var unchanged = await creator.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } });
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Nghỉ phép" } } }), HttpStatusCode.OK);

        Assert.Equal("Reason", noReason.Field());
        await noRight.ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        var record = await factory.QueryAsync(db => db.AttendanceRecords.AsNoTracking().SingleAsync(r => r.WorkerId == worker.Id && r.WorkDate == date));
        Assert.Equal(AttendanceResult.PaidLeave, record.Result);
        var audit = await factory.AuditOfAsync(record.Id);
        Assert.Equal(["AttendanceRecord.Created", "AttendanceRecord.Updated"], audit.Select(e => e.Action));
        Assert.Equal("Nghỉ phép", audit[1].Reason);
    }

    [Fact]
    public async Task Date_in_a_finalized_month_is_locked()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var month = await w.FinalizedMonthAsync();

        await (await w.Owner.PutAsJsonAsync(Sheet(w, month.Day(14)), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }))
            .ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        var sheet = await ExpectAsync(await w.Owner.GetAsync($"{w.Attendance}?date={month.Day(14):yyyy-MM-dd}"), HttpStatusCode.OK);
        Assert.True(sheet.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Zone_not_granted_is_MSG02_and_evidence_is_added_to_a_saved_record()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(13);
        var other = await factory.AddZoneAsync(NewCode());
        var (manager, _) = await factory.StaffAsync(other, (PermissionFunctions.Attendance, true, true, true, false));
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);
        var recordId = await factory.QueryAsync(db => db.AttendanceRecords.Where(r => r.WorkerId == worker.Id && r.WorkDate == date).Select(r => r.Id).SingleAsync());
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "proof.png");

        await (await manager.GetAsync($"{w.Attendance}?date={date:yyyy-MM-dd}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        var row = await ExpectAsync(await w.Owner.PostAsync($"{w.Attendance}/records/{recordId}/evidence", form), HttpStatusCode.OK);

        Assert.Equal(1, row.GetProperty("evidenceUrls").GetArrayLength());
    }

    private static List<JsonElement> Rows(JsonElement sheet) => sheet.GetProperty("rows").EnumerateArray().ToList();
}
