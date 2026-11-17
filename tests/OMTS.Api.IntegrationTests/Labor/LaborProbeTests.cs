using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Identity;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborProbeKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// Probes of the LABOR code merged by duyth (LABOR-02, LABOR-04, LABOR-05, LABOR-10) against four suspected weaknesses: (a) zone checks of
/// a worker whose current job period is on a raft of another zone, (b) the Owner correction of a day, (c) the attendance QR code,
/// (d) employee requests. Each probe asserts the SAFE behaviour the SRS (or the stricter reading, where the SRS is silent) asks for, so a
/// failing probe is a finding, not a broken test. The probes that need SQL Server are in <see cref="LaborProbeSqlServerTests"/>.
/// </summary>
public class LaborProbeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // ------------------------------------------------------------------------------------------------------------------------------
    // (a) Zone of a worker whose current job period is on a raft of another zone.
    // The API names an assignment (the FIRST job period of the day), not a raft: the zone checked is the zone of that first period.
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): a ZM of the first period zone can still check out a worker whose current period is in another zone. Remove the Skip when it is fixed.")]
    public async Task Probe_a1_check_out_is_refused_when_the_current_job_period_is_on_a_raft_of_another_zone()
    {
        // FAILURE MEANS: a Zone Manager of zone A can check out (and complete the day of) a worker who now works on a raft of zone B, because the zone is read from the first period of the day, not the current one.
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);
        await (await CheckInAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);
        await MoveCrewAsync(s, s.OtherRaft, employee);

        var response = await CheckOutAsync(s.Manager, row);

        await AssertForbiddenAsync(response);
        Assert.Null((await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee))).CheckOutAt);
    }

    [Fact]
    public async Task Probe_a1_control_the_zone_manager_of_the_other_zone_can_check_out_the_worker_who_works_on_their_raft()
    {
        // FAILURE MEANS: nobody in zone B can check out a worker moved onto zone B's raft (the row id is zone A's first period), so the refusal of a1 also locks out the right manager (control of a1, expected to pass).
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);
        await (await CheckInAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);
        await MoveCrewAsync(s, s.OtherRaft, employee);
        var managerB = await ManagerAsync(factory, s.OtherZone);
        var board = await managerB.GetFromJsonAsync<JsonElement>($"{Workforce}/assignments?day={Day(Today())}");
        var rowOfB = Assert.Single(board.EnumerateArray().Where(r => r.GetProperty("employeeId").GetGuid() == employee)).GetProperty("id").GetGuid();

        var response = await CheckOutAsync(managerB, rowOfB);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): check-out without check-in does not look at the zone of the current job period. Remove the Skip when it is fixed.")]
    public async Task Probe_a2_check_out_without_a_check_in_is_refused_when_the_current_job_period_is_on_a_raft_of_another_zone()
    {
        // FAILURE MEANS: the MSG-LABOR-12 path (check-out saved with no check-in) lets a Zone Manager of zone A write attendance for a worker whose current period is in zone B.
        var (s, employee, first) = await SplitAcrossZonesAsync();

        var response = await CheckOutAsync(s.Manager, first);

        await AssertForbiddenAsync(response);
        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == employee)));
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): check-in does not look at the zone of the current job period. Remove the Skip when it is fixed.")]
    public async Task Probe_a3_check_in_is_refused_when_the_current_job_period_is_on_a_raft_of_another_zone()
    {
        // FAILURE MEANS: a Zone Manager of zone A can check in a worker whose current period is on a raft of zone B (LABOR-04 step 3: work assigned on THIS raft).
        var (s, employee, first) = await SplitAcrossZonesAsync();

        var response = await CheckInAsync(s.Manager, first);

        await AssertForbiddenAsync(response);
        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == employee)));
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): a ZM can move a worker off a raft of another zone. Remove the Skip when it is fixed.")]
    public async Task Probe_a4_a_zone_manager_cannot_move_a_worker_off_a_raft_of_another_zone()
    {
        // FAILURE MEANS: the Zone Manager of zone B can take a not-yet-checked-in worker off zone A's raft with moveExisting (only the target raft's zone is checked). The SRS (LABOR-02.4) does not restrict the source zone; this asserts the stricter reading.
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        await AssignTodayAsync(s, s.Raft, s.Harvest, employee);
        var managerB = await ManagerAsync(factory, s.OtherZone);

        var move = await AssignAsync(managerB, s.OtherRaft.Id, s.Harvest, Today(), true, employee);

        await AssertForbiddenAsync(move);
        Assert.Equal(s.Raft.Id, (await factory.QueryAsync(db => db.WorkAssignments.SingleAsync(w => w.EmployeeId == employee))).RaftId);
    }

    [Fact]
    public async Task Probe_a5_change_job_for_crew_is_refused_for_a_worker_whose_current_raft_is_in_another_zone()
    {
        // FAILURE MEANS: the Zone Manager of zone B can change the job of a crew member who works on zone A's raft (expected safe: the loop checks the zone of the current period).
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        await AssignTodayAsync(s, s.Raft, s.Harvest, employee);
        var managerB = await ManagerAsync(factory, s.OtherZone);

        var response = await managerB.PostAsJsonAsync($"{Workforce}/crew-job", new { raftId = s.OtherRaft.Id, jobTypeId = s.Hanging, employeeIds = new[] { employee } });

        await AssertForbiddenAsync(response);
    }

    [Fact]
    public async Task Probe_a6_crew_start_naming_a_raft_of_zone_a_is_refused_for_a_worker_whose_current_period_is_in_zone_b()
    {
        // FAILURE MEANS: crew-start (which names a raft) checks in a worker whose current period is on another zone's raft (expected safe: members are matched on their latest period).
        var (s, employee, _) = await SplitAcrossZonesAsync();

        var response = await s.Manager.PostAsJsonAsync($"{Workforce}/crew-start", new { raftId = s.Raft.Id, startTime = "00:00", reason = "Late arrival", employeeIds = new[] { employee } });

        await AssertRefusedAsync(response, "crew-start of a worker whose current period is in zone B");
        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == employee)));
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // (b) Owner correction of a day (the SQL Server probes b1 real / b1b / b1d / b2 are in LaborProbeSqlServerTests).
    // The in-memory provider issues no row versions, so these probes store one on the record as SQL Server would.
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Probe_b1_a_correction_with_a_version_read_before_another_correction_is_refused_with_msg06()
    {
        // FAILURE MEANS: an Owner tab that read the day before another correction can overwrite that correction (lost update); the real-version twin is in LaborProbeSqlServerTests.
        await using var own = new AuthApiFactory();
        var s = await SetupWorkAsync(own);
        var employee = await AddEmployeeAsync(s);
        var day = new DateOnly(2026, 3, 2);
        object[] periods = [PeriodOf(s.Harvest, s.Raft.Id, day, null)];
        await (await s.Owner.PutAsJsonAsync(DayUrl(employee, day), Correction(day, "06:00", "12:00", periods, null))).BodyAsync(HttpStatusCode.OK);
        var recordId = (await own.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee))).Id;
        var screen = await SetVersionAsync(own, recordId, 1);
        await (await s.Owner.PutAsJsonAsync(DayUrl(employee, day), Correction(day, "06:00", "13:00", periods, screen))).BodyAsync(HttpStatusCode.OK);
        await SetVersionAsync(own, recordId, 2);

        var stale = await s.Owner.PutAsJsonAsync(DayUrl(employee, day), Correction(day, "06:00", "14:00", periods, screen));

        Assert.Equal(("MSG06", HttpStatusCode.Conflict), (await stale.ErrorCodeAsync(), stale.StatusCode));
        Assert.Equal(DateTimeOffset.Parse(Time(day, "13:00")), (await own.QueryAsync(db => db.Attendances.SingleAsync(a => a.Id == recordId))).CheckOutAt);
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): a correction of a day that got a record after the screen was read answers 400 MSG-GEN-01, not 409 MSG06. Remove the Skip when it is fixed.")]
    public async Task Probe_b1b_a_correction_of_a_day_that_got_a_record_after_the_screen_was_read_is_refused_with_msg06()
    {
        // FAILURE MEANS: the screen of a day with no record (empty version) is not recognised as stale once a check-in exists: it answers 400 RowVersion (or overwrites it) instead of 409 MSG06. Strict reading of NFR-09.
        await using var own = new AuthApiFactory();
        var s = await SetupWorkAsync(own);
        var employee = await AddEmployeeAsync(s);
        var today = Today();
        var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);
        var screen = await s.Owner.GetFromJsonAsync<JsonElement>(DayUrl(employee, today));
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("rowVersion").ValueKind);
        await (await CheckInAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);
        var recordId = (await own.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee))).Id;
        await SetVersionAsync(own, recordId, 1);
        object[] periods = [PeriodOf(s.Harvest, s.Raft.Id, today, null)];

        var stale = await s.Owner.PutAsJsonAsync(DayUrl(employee, today), Correction(today, "00:30", "01:00", periods, ""));

        Assert.Equal(("MSG06", HttpStatusCode.Conflict), (await stale.ErrorCodeAsync(), stale.StatusCode));
        Assert.Equal(AttendanceMethod.Manual, (await own.QueryAsync(db => db.Attendances.SingleAsync(a => a.Id == recordId))).CheckInMethod);
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): a correction silently removes a job period added after the screen was read. Remove the Skip when it is fixed.")]
    public async Task Probe_b3_a_correction_does_not_silently_remove_a_job_period_added_after_the_screen_was_read()
    {
        // FAILURE MEANS: the Owner's correction replaces the job periods with the list on the stale screen, so a period added meanwhile by Change job for crew is deleted without a word (the attendance row version does not change when a period is added).
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);
        await (await CheckInAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);
        var screen = await s.Owner.GetFromJsonAsync<JsonElement>(DayUrl(employee, Today()));
        Assert.Equal(1, screen.GetProperty("periods").GetArrayLength());
        var crew = await s.Manager.PostAsJsonAsync($"{Workforce}/crew-job", new { raftId = s.Raft2.Id, jobTypeId = s.Hanging, employeeIds = new[] { employee } });
        Assert.Equal(HttpStatusCode.NoContent, crew.StatusCode);
        var addedId = Assert.Single(await factory.QueryAsync(db => db.WorkAssignments.Where(w => w.EmployeeId == employee && w.StartsAt != null).Select(w => w.Id).ToListAsync()));
        var staleBody = new
        {
            checkInAt = screen.Str("checkedInAt"),
            checkOutAt = (string?)null,
            periods = new object[] { new { jobTypeId = s.Harvest, raftId = s.Raft.Id, startsAt = (string?)null } },
            reason = "Fix the check-in time",
            rowVersion = screen.Str("rowVersion"),
        };

        var response = await s.Owner.PutAsJsonAsync(DayUrl(employee, Today()), staleBody);

        var survives = await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(w => w.Id == addedId));
        Assert.True(survives, $"The job period added after the screen was read was removed (the correction answered {(int)response.StatusCode}).");
        Assert.Equal(("MSG06", HttpStatusCode.Conflict), (await response.ErrorCodeAsync(), response.StatusCode));
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // (c) Attendance QR code: signed, names the employee, valid 60 s. What else is it bound to? (device, single use)
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Probe_c1_the_same_qr_code_scanned_twice_for_a_check_in_is_refused_as_already_recorded()
    {
        // FAILURE MEANS: a replayed scan within the 60 s window records the check-in again or moves its time (it should answer 409 MSG-LABOR-14 and keep the first time).
        var (s, employee, row, client) = await LinkedAssignedAsync();
        var code = await QrOfAsync(client);
        var first = await (await CheckInAsync(s.Manager, row, code)).BodyAsync(HttpStatusCode.OK);

        var replay = await CheckInAsync(s.Manager, row, code);

        Assert.Equal(("MSG-LABOR-14", HttpStatusCode.Conflict), (await replay.ErrorCodeAsync(), replay.StatusCode));
        var record = await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee));
        Assert.Equal(DateTimeOffset.Parse(first.Str("checkedInAt")!), record.CheckInAt);
    }

    [Fact]
    public async Task Probe_c1b_the_same_qr_code_scanned_twice_for_a_check_out_is_refused_as_already_recorded()
    {
        // FAILURE MEANS: a replayed scan within the window records the check-out again or moves its time (it should answer 409 MSG-LABOR-14 and keep the first time).
        var (s, employee, row, client) = await LinkedAssignedAsync();
        await (await CheckInAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);
        var code = await QrOfAsync(client);
        var first = await (await CheckOutAsync(s.Manager, row, code)).BodyAsync(HttpStatusCode.OK);

        var replay = await CheckOutAsync(s.Manager, row, code);

        Assert.Equal(("MSG-LABOR-14", HttpStatusCode.Conflict), (await replay.ErrorCodeAsync(), replay.StatusCode));
        var record = await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee));
        Assert.Equal(DateTimeOffset.Parse(first.Str("checkedOutAt")!), record.CheckOutAt);
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): the QR code of a check-in can be used again for the check-out inside its window. Remove the Skip when it is fixed.")]
    public async Task Probe_c2_a_qr_code_used_for_the_check_in_cannot_be_used_again_for_the_check_out()
    {
        // FAILURE MEANS: the code is not single-use: one photo of it taken at check-in also checks the employee OUT inside its 60 s (BR-LABOR-06 only promises "replaced every 60 seconds"; this asserts the stricter single-use reading).
        var (s, employee, row, client) = await LinkedAssignedAsync();
        var code = await QrOfAsync(client);
        await (await CheckInAsync(s.Manager, row, code)).BodyAsync(HttpStatusCode.OK);

        var response = await CheckOutAsync(s.Manager, row, code);

        await AssertRefusedAsync(response, "check-out with the code already used for the check-in");
        Assert.Null((await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee))).CheckOutAt);
    }

    [Fact]
    public async Task Probe_c3_the_qr_code_of_one_employee_cannot_record_another_employee()
    {
        // FAILURE MEANS: a code of employee X is accepted for employee Y, on a crew row (check-in, check-out) or on the staff list.
        var s = await SetupWorkAsync(factory);
        var (x, xClient) = await AddLinkedEmployeeAsync(s);
        var y = await AddEmployeeAsync(s);
        var rows = await AssignTodayAsync(s, s.Raft, s.Harvest, x, y);
        var (xStaff, xStaffClient) = await AddLinkedEmployeeAsync(s, staff: true);
        var yStaff = await AddEmployeeAsync(s, staff: true);
        var code = await QrOfAsync(xClient);
        var staffCode = await QrOfAsync(xStaffClient);

        var responses = new (string What, HttpResponseMessage Response)[]
        {
            ("check-in", await CheckInAsync(s.Manager, RowId(rows, y), code)),
            ("check-out", await CheckOutAsync(s.Manager, RowId(rows, y), code)),
            ("staff check-in", await s.Manager.PostAsJsonAsync($"{Workforce}/staff/{yStaff}/check-in", new { qrCode = staffCode })),
            ("staff check-out", await s.Manager.PostAsJsonAsync($"{Workforce}/staff/{yStaff}/check-out", new { qrCode = staffCode })),
        };

        foreach (var (what, response) in responses)
        {
            Assert.Equal((what, "MSG-LABOR-13", HttpStatusCode.BadRequest), (what, await response.ErrorCodeAsync(), response.StatusCode));
        }

        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == y || a.EmployeeId == yStaff || a.EmployeeId == x || a.EmployeeId == xStaff)));
    }

    [Fact]
    public async Task Probe_c4_a_qr_code_older_than_its_window_is_refused()
    {
        // FAILURE MEANS: a code issued 61 s, 10 minutes or a day ago still records attendance (expiry is not enforced).
        var s = await SetupWorkAsync(factory);
        foreach (var age in new[] { TimeSpan.FromSeconds(61), TimeSpan.FromMinutes(10), TimeSpan.FromDays(1) })
        {
            var employee = await AddEmployeeAsync(s);
            var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);

            var response = await CheckInAsync(s.Manager, row, CodeIssued(employee, -age));

            Assert.Equal((age, "MSG-LABOR-13", HttpStatusCode.BadRequest), (age, await response.ErrorCodeAsync(), response.StatusCode));
            Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == employee)));
        }
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): a QR code issued in the future is accepted. Remove the Skip when it is fixed.")]
    public async Task Probe_c5_a_qr_code_issued_in_the_future_is_refused()
    {
        // FAILURE MEANS: Read() only checks "now < expiry", so a genuine code whose expiry is far beyond the 60 s lifetime (issued 5 minutes or a day ahead of now) is accepted; the window has no upper bound. Unspecified by the SRS; stricter reading.
        var s = await SetupWorkAsync(factory);
        foreach (var ahead in new[] { TimeSpan.FromMinutes(5), TimeSpan.FromDays(1) })
        {
            var employee = await AddEmployeeAsync(s);
            var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);

            var response = await CheckInAsync(s.Manager, row, CodeIssued(employee, ahead));

            Assert.Equal((ahead, "MSG-LABOR-13", HttpStatusCode.BadRequest), (ahead, await response.ErrorCodeAsync(), response.StatusCode));
            Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == employee)));
        }
    }

    [Fact]
    public async Task Probe_c6_a_qr_code_cannot_record_an_employee_who_has_left()
    {
        // FAILURE MEANS: a code obtained while the employee was Employed still checks them in after they Left (status Left) or after their last working day has passed (status not yet switched by the job).
        var s = await SetupWorkAsync(factory);
        foreach (var how in new[] { "status Left", "last working day passed" })
        {
            var (employee, client) = await AddLinkedEmployeeAsync(s);
            var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);
            var code = await QrOfAsync(client);
            await factory.UpdateAsync<OMTS.Domain.Entities.Identity.Employee>(employee, e =>
            {
                if (how == "status Left")
                {
                    e.StatusId = StatusIds.Employee.Left;
                }
                else
                {
                    e.LastWorkingDay = Today().AddDays(-1);
                }
            });

            var response = await CheckInAsync(s.Manager, row, code);

            Assert.Equal((how, "MSG-GEN-09"), (how, await response.ErrorCodeAsync()));
            Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == employee)));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // (d) Employee requests (leave, missed check-in, missed check-out). The SQL Server probes d6 and d7 are in LaborProbeSqlServerTests.
    // ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Probe_d1_a_request_touching_a_day_of_a_finalized_payroll_is_refused_for_every_type_and_range()
    {
        // FAILURE MEANS: an employee can send a leave or missed-time request that touches a Finalized pay period (MSG-LABOR-25, BR-LABOR-08), for one of the listed shapes (inside, first/last day, overlapping an end, spanning the whole period).
        await using var own = new AuthApiFactory();
        var s = await SetupWorkAsync(own);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var today = Today();
        await own.AddPayrollAsync(Day(today.AddDays(-12)), Day(today.AddDays(-6)), StatusIds.Payroll.Finalized);
        var refused = new (string What, object Body)[]
        {
            ("missed check-in inside", Missed("MissedCheckIn", today.AddDays(-8), "06:00")),
            ("missed check-out on the first day", Missed("MissedCheckOut", today.AddDays(-12), "16:00")),
            ("missed check-in on the last day", Missed("MissedCheckIn", today.AddDays(-6), "06:00")),
            ("leave inside", Leave(today.AddDays(-9), today.AddDays(-8))),
            ("leave overlapping the start", Leave(today.AddDays(-14), today.AddDays(-10))),
            ("leave overlapping the end", Leave(today.AddDays(-7), today.AddDays(-4))),
            ("leave spanning the period", Leave(today.AddDays(-20), today.AddDays(-2))),
        };

        foreach (var (what, body) in refused)
        {
            var response = await Submit(mine, body);
            Assert.Equal((what, "MSG-LABOR-25", HttpStatusCode.Conflict), (what, await response.ErrorCodeAsync(), response.StatusCode));
        }

        Assert.False(await own.QueryAsync(db => db.EmployeeRequests.AnyAsync(r => r.EmployeeId == employee)));
        // Controls: the days just before and just after the period are not finalized, so they are accepted.
        Assert.Equal(HttpStatusCode.Created, (await Submit(mine, Leave(today.AddDays(-16), today.AddDays(-13)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Submit(mine, Leave(today.AddDays(-5), today.AddDays(-4)))).StatusCode);
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): a request for a day in 2001 is accepted (no age limit). Remove the Skip when it is fixed.")]
    public async Task Probe_d2_a_request_for_a_day_in_2001_is_refused()
    {
        // FAILURE MEANS: the request has no lower bound on its day: an employee can send leave or a missed time for 2001-01-01 (unspecified by the SRS; stricter reading: a request must be for a plausible, recent day).
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var old = new DateOnly(2001, 1, 1);
        var requests = new (string What, object Body)[]
        {
            ("leave", Leave(old, old.AddDays(1))),
            ("missed check-in", Missed("MissedCheckIn", old, "06:00")),
            ("missed check-out", Missed("MissedCheckOut", old, "16:00")),
        };

        foreach (var (what, body) in requests)
        {
            await AssertRefusedAsync(await Submit(mine, body), what);
        }

        Assert.False(await factory.QueryAsync(db => db.EmployeeRequests.AnyAsync(r => r.EmployeeId == employee)));
    }

    [Fact(Skip = "Finding of the review of 13/11 (OMTS-227): a request for a day one year ago is accepted (no age limit). Remove the Skip when it is fixed.")]
    public async Task Probe_d2b_a_request_for_a_day_one_year_ago_is_refused()
    {
        // FAILURE MEANS: a day a year back is accepted (unspecified by the SRS, which only bars days of a Finalized payroll; stricter reading: a bounded look-back). A failure here is a decision to make, not necessarily a bug.
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var old = Today().AddYears(-1);
        var requests = new (string What, object Body)[]
        {
            ("leave", Leave(old, old.AddDays(1))),
            ("missed check-in", Missed("MissedCheckIn", old, "06:00")),
            ("missed check-out", Missed("MissedCheckOut", old, "16:00")),
        };

        foreach (var (what, body) in requests)
        {
            await AssertRefusedAsync(await Submit(mine, body), what);
        }

        Assert.False(await factory.QueryAsync(db => db.EmployeeRequests.AnyAsync(r => r.EmployeeId == employee)));
    }

    [Fact]
    public async Task Probe_d3_a_leave_longer_than_366_days_is_refused()
    {
        // FAILURE MEANS: the 366-day limit of leave is off by one or missing (367 days, or ten years, is accepted).
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var start = Today().AddDays(20);

        foreach (var extraDays in new[] { 366, 3650 })
        {
            var response = await Submit(mine, Leave(start, start.AddDays(extraDays)));
            Assert.Equal((extraDays, "Days"), (extraDays, await FieldAsync(response)));
        }

        Assert.False(await factory.QueryAsync(db => db.EmployeeRequests.AnyAsync(r => r.EmployeeId == employee)));
    }

    [Fact]
    public async Task Probe_d4_a_second_awaiting_request_for_the_same_day_and_type_is_refused_and_names_the_first()
    {
        // FAILURE MEANS: an employee can have two Awaiting-review requests for the same day and type (identical, inside, containing or sharing a day with the first), or the 409 does not name the first (MSG-GEN-02, LABOR-10.0.E3).
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var d = Today().AddDays(10);
        var first = await (await Submit(mine, Leave(d.AddDays(5), d.AddDays(7)))).BodyAsync(HttpStatusCode.Created);
        var missed = await (await Submit(mine, Missed("MissedCheckIn", Today().AddDays(-1), "06:00"))).BodyAsync(HttpStatusCode.Created);
        var attempts = new (string What, object Body, JsonElement Existing)[]
        {
            ("the same leave days", Leave(d.AddDays(5), d.AddDays(7)), first),
            ("a day inside the leave", Leave(d.AddDays(6), d.AddDays(6)), first),
            ("days containing the leave", Leave(d.AddDays(3), d.AddDays(9)), first),
            ("sharing the last day", Leave(d.AddDays(7), d.AddDays(8)), first),
            ("the same missed check-in at another time", Missed("MissedCheckIn", Today().AddDays(-1), "07:30"), missed),
        };

        foreach (var (what, body, existing) in attempts)
        {
            var response = await Submit(mine, body);
            Assert.Equal((what, "MSG-GEN-02", HttpStatusCode.Conflict), (what, await response.ErrorCodeAsync(), response.StatusCode));
            Assert.Equal(existing.GetProperty("id").GetGuid(), (await ProblemAsync(response)).GetProperty("existingId").GetGuid());
        }

        Assert.Equal(2, await factory.QueryAsync(db => db.EmployeeRequests.CountAsync(r => r.EmployeeId == employee)));
    }

    [Fact]
    public async Task Probe_d5_an_employee_cannot_cancel_a_request_the_owner_already_reviewed()
    {
        // FAILURE MEANS: an approved or rejected request can be cancelled (409 MSG-LABOR-26 expected) or its status/cancellation time changes after the review (BR-LABOR-08: a reviewed request does not change any more).
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var approved = await (await Submit(mine, Leave(Today().AddDays(30), Today().AddDays(30)))).BodyAsync(HttpStatusCode.Created);
        var rejected = await (await Submit(mine, Leave(Today().AddDays(32), Today().AddDays(32)))).BodyAsync(HttpStatusCode.Created);
        await (await Review(s.Owner, approved, true)).BodyAsync(HttpStatusCode.OK);
        await (await Review(s.Owner, rejected, false, "Harvest season")).BodyAsync(HttpStatusCode.OK);

        foreach (var (what, request, status) in new[] { ("approved", approved, StatusIds.EmployeeRequest.Approved), ("rejected", rejected, StatusIds.EmployeeRequest.Rejected) })
        {
            var id = request.GetProperty("id").GetGuid();
            var response = await mine.DeleteAsync($"{Me}/leave-requests/{id}");

            Assert.Equal((what, "MSG-LABOR-26", HttpStatusCode.Conflict), (what, await response.ErrorCodeAsync(), response.StatusCode));
            var stored = await factory.QueryAsync(db => db.EmployeeRequests.SingleAsync(r => r.Id == id));
            Assert.Equal((what, status, (DateTimeOffset?)null), (what, stored.StatusId, stored.CancelledAt));
        }

        Assert.Equal(0, await factory.QueryAsync(db => db.EmployeeRequests.CountAsync(r => r.EmployeeId == employee && r.StatusId == StatusIds.EmployeeRequest.Cancelled)));
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------------------------------------

    private static async Task MoveCrewAsync(WorkSetup s, Raft raft, Guid employee)
    {
        // The Owner works in every zone, so Change job for crew can put a checked-in worker on a raft of another zone.
        var response = await s.Owner.PostAsJsonAsync($"{Workforce}/crew-job", new { raftId = raft.Id, jobTypeId = s.Hanging, employeeIds = new[] { employee } });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// A seasonal worker whose first job period of today is on a raft of zone A and whose current (latest) one is on a raft of zone B, with
    /// no attendance yet. The API cannot make that state (Change job for crew rewrites the first period of a worker not yet checked in), so
    /// it is written to the database, as the correction tests write past days.
    /// </summary>
    private async Task<(WorkSetup S, Guid Employee, Guid FirstPeriod)> SplitAcrossZonesAsync()
    {
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        await AddPastDayAsync(s, employee, Today(), null, null, (s.Harvest, s.Raft.Id, null), (s.Hanging, s.OtherRaft.Id, "00:00"));
        var first = await factory.QueryAsync(db => db.WorkAssignments.Where(w => w.EmployeeId == employee && w.StartsAt == null).Select(w => w.Id).SingleAsync());
        return (s, employee, first);
    }

    private async Task<(WorkSetup S, Guid Employee, Guid Row, HttpClient Client)> LinkedAssignedAsync()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, client) = await AddLinkedEmployeeAsync(s);
        var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);
        return (s, employee, row, client);
    }

    private static async Task<string> QrOfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>($"{Me}/attendance-qr")).Str("code")!;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A genuine, signed code of the employee as if it had been issued <paramref name="offset"/> from now (negative: in the past), with the host's own signing key.</summary>
    private string CodeIssued(Guid employeeId, TimeSpan offset) =>
        new HmacAttendanceQrCodes(factory.Services.GetRequiredService<IOptions<JwtOptions>>(), new FixedClock(DateTimeOffset.UtcNow + offset)).Issue(employeeId).Code;

    /// <summary>The in-memory provider issues no row versions: stores one on the record, as SQL Server would, and returns it as the client reads it.</summary>
    private static async Task<string> SetVersionAsync(AuthApiFactory host, Guid attendanceId, byte last)
    {
        byte[] version = [0, 0, 0, 0, 0, 0, 7, last];
        await host.UpdateAsync<Attendance>(attendanceId, a => a.RowVersion = version);
        return Convert.ToBase64String(version);
    }
}

/// <summary>Request builders and assertions the probes share (also used by <see cref="LaborProbeSqlServerTests"/>).</summary>
internal static class LaborProbeKit
{
    public static string DayUrl(Guid employee, DateOnly day) => $"{WorkTestKit.Workforce}/attendance/{employee}/{WorkTestKit.Day(day)}";

    public static object PeriodOf(Guid jobTypeId, Guid raftId, DateOnly day, string? startsAt) =>
        new { jobTypeId, raftId, startsAt = startsAt is null ? null : WorkTestKit.Time(day, startsAt) };

    public static object Correction(DateOnly day, string checkIn, string? checkOut, object[] periods, string? rowVersion, string reason = "Forgot to clock") => new
    {
        checkInAt = WorkTestKit.Time(day, checkIn),
        checkOutAt = checkOut is null ? null : WorkTestKit.Time(day, checkOut),
        periods,
        reason,
        rowVersion,
    };

    public static Task<HttpResponseMessage> Submit(HttpClient client, object body) => client.PostAsJsonAsync($"{WorkTestKit.Me}/leave-requests", body);

    public static object Leave(DateOnly from, DateOnly to, Guid? op = null) =>
        new { from = WorkTestKit.Day(from), to = WorkTestKit.Day(to), reason = "Family matter", clientOperationId = op ?? Guid.NewGuid() };

    public static object Missed(string type, DateOnly day, string time) =>
        new { type, day = WorkTestKit.Day(day), requestedTime = time, reason = "Forgot to clock", clientOperationId = Guid.NewGuid() };

    /// <summary>The Owner approves or rejects the request as the list showed it (its row version).</summary>
    public static Task<HttpResponseMessage> Review(HttpClient owner, JsonElement request, bool approved, string? note = null) =>
        owner.PostAsJsonAsync($"{WorkTestKit.Workforce}/leave-requests/{request.GetProperty("id").GetGuid()}/review",
            new { approved, response = note, rowVersion = MessageCodeQaKit.Str(request, "rowVersion") });

    /// <summary>403 MSG-GEN-08. The status is checked first: a success answer can be an array or empty, which the code reader cannot parse.</summary>
    public static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403 MSG-GEN-08, got {(int)response.StatusCode}: {text}");
        Assert.Equal("MSG-GEN-08", await AcsTestKit.ErrorCodeAsync(response));
    }

    /// <summary>Any 4xx: the SRS names no code for the case, only that nothing is recorded.</summary>
    public static async Task AssertRefusedAsync(HttpResponseMessage response, string what)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode is >= 400 and < 500, $"{what}: expected a 4xx refusal, got {(int)response.StatusCode}: {text}");
    }
}
