using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-04 stale edits (review): changing an already saved attendance record needs the rowVersion the client read, as
/// everywhere else: missing → MSG01 "RowVersion", stale → MSG06. Team rows send the versions of their members in
/// <c>memberRowVersions</c>. InMemory EF issues no rowversions, so a stale one is a version the server never issued.
/// </summary>
public class LaborAttendanceRowVersionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly string NeverIssued = Convert.ToBase64String([9, 9, 9, 9, 9, 9, 9, 9]);

    private static string Sheet(LaborWorld w, DateOnly date) => $"{w.Attendance}/{date:yyyy-MM-dd}";

    private Task<AttendanceResult> ResultOfAsync(Guid personId, DateOnly date) =>
        factory.QueryAsync(db => db.AttendanceRecords.AsNoTracking().Where(r => (r.WorkerId ?? r.EmployeeId) == personId && r.WorkDate == date).Select(r => r.Result).SingleAsync());

    [Fact]
    public async Task An_individual_row_changing_a_saved_record_without_rowVersion_is_MSG01_and_a_stale_one_is_MSG06()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var employee = await w.AddEmployeeAsync();
        var date = NewMonth().Day(12);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new
        {
            rows = new[] { new { workerId = (Guid?)worker.Id, employeeId = (Guid?)null, result = "Present" }, new { workerId = (Guid?)null, employeeId = (Guid?)employee.Id, result = "Present" } },
        }), HttpStatusCode.OK);

        var missing = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Ốm" } } }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var missingEmployee = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { employeeId = employee.Id, result = "PaidLeave", reason = "Ốm" } } }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var stale = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Ốm", rowVersion = NeverIssued } } }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var current = await w.AttendanceVersionAsync(date, worker.Id);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Ốm", rowVersion = current } } }), HttpStatusCode.OK);

        Assert.Equal(("RowVersion", "RowVersion"), (missing.Field(), missingEmployee.Field()));
        Assert.Equal(1, stale.GetProperty("rows").GetArrayLength());
        Assert.Equal((AttendanceResult.PaidLeave, AttendanceResult.Present), (await ResultOfAsync(worker.Id, date), await ResultOfAsync(employee.Id, date)));
    }

    [Fact]
    public async Task A_row_that_changes_nothing_or_creates_a_record_needs_no_rowVersion()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var other = await w.AddWorkerAsync();
        var date = NewMonth().Day(12);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);

        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new
        {
            rows = new[] { new { workerId = worker.Id, result = "Present" }, new { workerId = other.Id, result = "Present" } },
        }), HttpStatusCode.OK);

        Assert.Equal(AttendanceResult.Present, await ResultOfAsync(other.Id, date));
    }

    [Fact]
    public async Task A_team_row_changing_saved_member_records_needs_every_member_rowVersion_missing_is_MSG01_stale_is_MSG06()
    {
        var w = await factory.WorldAsync();
        var team = await w.AddTeamAsync();
        var first = await w.AddWorkerAsync(team.Id);
        var second = await w.AddWorkerAsync(team.Id);
        var date = NewMonth().Day(12);
        // Creating the records through the team row needs no versions.
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { teamId = team.Id, result = "Present" } } }), HttpStatusCode.OK);
        var versions = new Dictionary<Guid, string>
        {
            [first.Id] = await w.AttendanceVersionAsync(date, first.Id),
            [second.Id] = await w.AttendanceVersionAsync(date, second.Id),
        };
        object Change(IReadOnlyDictionary<Guid, string>? memberRowVersions) =>
            new { rows = new[] { new { teamId = team.Id, result = "UnpaidLeave", reason = "Bão" } }, memberRowVersions };

        var none = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), Change(null))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var onlyOne = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), Change(new Dictionary<Guid, string> { [first.Id] = versions[first.Id] }))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var stale = await (await w.Owner.PutAsJsonAsync(Sheet(w, date), Change(new Dictionary<Guid, string> { [first.Id] = versions[first.Id], [second.Id] = NeverIssued })))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        Assert.Equal(("RowVersion", "RowVersion"), (none.Field(), onlyOne.Field()));
        Assert.Equal(AttendanceResult.Present, await ResultOfAsync(first.Id, date));
        Assert.Equal(AttendanceResult.Present, await ResultOfAsync(second.Id, date));
        Assert.NotNull(stale.GetProperty("rows")[0].GetProperty("person").GetString());

        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), Change(versions)), HttpStatusCode.OK);
        Assert.Equal((AttendanceResult.UnpaidLeave, AttendanceResult.UnpaidLeave), (await ResultOfAsync(first.Id, date), await ResultOfAsync(second.Id, date)));
    }

    [Fact]
    public async Task An_individual_row_overriding_a_team_row_uses_its_own_rowVersion()
    {
        var w = await factory.WorldAsync();
        var team = await w.AddTeamAsync();
        var first = await w.AddWorkerAsync(team.Id);
        var second = await w.AddWorkerAsync(team.Id);
        var date = NewMonth().Day(13);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(Sheet(w, date), new { rows = new[] { new { teamId = team.Id, result = "Present" } } }), HttpStatusCode.OK);
        var firstVersion = await w.AttendanceVersionAsync(date, first.Id);
        var secondVersion = await w.AttendanceVersionAsync(date, second.Id);

        var response = await w.Owner.PutAsJsonAsync(Sheet(w, date), new
        {
            rows = new object[]
            {
                new { teamId = team.Id, result = "UnpaidLeave", reason = "Bão" },
                new { workerId = first.Id, result = "Present", rowVersion = firstVersion },
            },
            memberRowVersions = new Dictionary<Guid, string> { [second.Id] = secondVersion },
        });

        await ExpectAsync(response, HttpStatusCode.OK);
        Assert.Equal((AttendanceResult.Present, AttendanceResult.UnpaidLeave), (await ResultOfAsync(first.Id, date), await ResultOfAsync(second.Id, date)));
    }
}
