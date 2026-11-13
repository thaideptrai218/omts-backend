using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-10 and LABOR-05 with BR-LABOR-08: an employee sends and cancels requests, the Cooperative Owner approves or rejects them.</summary>
public class EmployeeRequestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, object body) => client.PostAsJsonAsync($"{Me}/leave-requests", body);

    private static object Leave(DateOnly from, DateOnly to, string reason = "Việc gia đình", Guid? op = null) =>
        new { from = Day(from), to = Day(to), reason, clientOperationId = op ?? Guid.NewGuid() };

    private static object Missed(string type, DateOnly day, string time, string reason = "Quên chấm công") =>
        new { type, day = Day(day), requestedTime = time, reason, clientOperationId = Guid.NewGuid() };

    private static Task<HttpResponseMessage> ReviewAsync(
        HttpClient client, JsonElement request, bool approved, string? response = null, Guid? jobTypeId = null, Guid? raftId = null) =>
        client.PostAsJsonAsync($"{Workforce}/leave-requests/{request.GetProperty("id").GetGuid()}/review",
            new { approved, response, rowVersion = request.Str("rowVersion"), jobTypeId, raftId });

    private static DateOnly Yesterday() => Today().AddDays(-1);

    [Fact]
    public async Task An_employee_sends_a_leave_request_and_the_owner_is_notified()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);

        var response = await SubmitAsync(mine, Leave(Today().AddDays(3), Today().AddDays(4)));
        var sent = await response.BodyAsync(HttpStatusCode.Created);

        Assert.Equal(("Leave", "Awaiting review", StatusIds.EmployeeRequest.AwaitingReview, "MSG-LABOR-22", "Your request has been sent to the Cooperative Owner."),
            (sent.Str("type"), sent.Str("status"), sent.GetProperty("statusId").GetInt32(), sent.Str("messageCode"), sent.Str("message")));
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("approved").ValueKind);
        Assert.Equal((Day(Today().AddDays(3)), Day(Today().AddDays(4)), "Việc gia đình"), (sent.Str("from"), sent.Str("to"), sent.Str("reason")));
        Assert.Equal(employee, sent.GetProperty("employeeId").GetGuid());
        var id = sent.GetProperty("id").GetGuid();
        var notice = await factory.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == id && n.Type == NotificationTypes.EmployeeRequestNew).ToListAsync());
        Assert.NotEmpty(notice);
        Assert.All(notice, n => Assert.Equal("MSG-LABOR-23", n.MessageCode));
        Assert.Contains("sent a request for leave from", notice[0].Body);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "EmployeeRequest.Submitted" && e.RecordId == id)));
    }

    [Fact]
    public async Task A_retry_with_the_same_client_operation_returns_the_request_already_saved()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var op = Guid.NewGuid();

        var first = await (await SubmitAsync(mine, Leave(Today(), Today(), op: op))).BodyAsync(HttpStatusCode.Created);
        var retry = await (await SubmitAsync(mine, Leave(Today(), Today(), op: op))).BodyAsync(HttpStatusCode.Created);

        Assert.Equal(first.GetProperty("id").GetGuid(), retry.GetProperty("id").GetGuid());
        Assert.Equal(1, await factory.QueryAsync(db => db.EmployeeRequests.CountAsync(r => r.EmployeeId == employee)));
    }

    [Fact]
    public async Task A_request_with_missing_or_invalid_data_is_refused_with_the_field_named()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);

        async Task<string?> FieldOf(object body) => await FieldAsync(await SubmitAsync(mine, body));

        Assert.Equal("Days", await FieldOf(Leave(Today().AddDays(2), Today().AddDays(1))));
        Assert.Equal("Reason", await FieldOf(Leave(Today(), Today(), reason: "  ")));
        Assert.Equal("Reason", await FieldOf(Leave(Today(), Today(), reason: new string('x', 501))));
        Assert.Equal("Days", await FieldOf(Leave(Today(), Today().AddDays(400))));
        Assert.Equal("Days", await FieldOf(new { reason = "x", clientOperationId = Guid.NewGuid() }));
        Assert.Equal("Requested time", await FieldOf(new { type = "MissedCheckIn", day = Day(Yesterday()), reason = "x", clientOperationId = Guid.NewGuid() }));
        Assert.Equal("Day (today or earlier)", await FieldOf(Missed("MissedCheckOut", Today().AddDays(1), "16:00")));
        var unknownType = await SubmitAsync(mine, new { type = "Nope", day = Day(Yesterday()), requestedTime = "06:00", reason = "x", clientOperationId = Guid.NewGuid() });
        Assert.Equal(("MSG-GEN-01", HttpStatusCode.BadRequest), (await unknownType.ErrorCodeAsync(), unknownType.StatusCode));
        Assert.False(await factory.QueryAsync(db => db.EmployeeRequests.AnyAsync(r => r.EmployeeId == employee)));
    }

    [Fact]
    public async Task A_request_needs_an_employee_profile_linked_to_the_account_and_a_working_employee()
    {
        var s = await SetupWorkAsync(factory);
        Assert.Equal("MSG-GEN-08", await (await SubmitAsync(s.Manager, Leave(Today(), Today()))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SubmitAsync(factory.CreateClient(), Leave(Today(), Today()))).StatusCode);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        await EndAsync(s.Owner, employee);

        Assert.Equal("MSG-GEN-09", await (await SubmitAsync(mine, Leave(Today(), Today()))).ErrorCodeAsync());
    }

    [Fact]
    public async Task A_second_request_for_the_same_days_or_the_same_missed_time_is_refused_and_names_the_first()
    {
        var s = await SetupWorkAsync(factory);
        var (_, mine) = await AddLinkedEmployeeAsync(s);
        var leave = await (await SubmitAsync(mine, Leave(Today().AddDays(5), Today().AddDays(7)))).BodyAsync(HttpStatusCode.Created);
        var missed = await (await SubmitAsync(mine, Missed("MissedCheckIn", Yesterday(), "06:00"))).BodyAsync(HttpStatusCode.Created);

        var overlapping = await SubmitAsync(mine, Leave(Today().AddDays(7), Today().AddDays(9)));
        var sameMissed = await SubmitAsync(mine, Missed("MissedCheckIn", Yesterday(), "07:00"));

        Assert.Equal(("MSG-GEN-02", HttpStatusCode.Conflict), (await overlapping.ErrorCodeAsync(), overlapping.StatusCode));
        Assert.Equal(leave.GetProperty("id").GetGuid(), (await ProblemAsync(overlapping)).GetProperty("existingId").GetGuid());
        Assert.Equal(missed.GetProperty("id").GetGuid(), (await ProblemAsync(sameMissed)).GetProperty("existingId").GetGuid());
        // Another day, the other missed time, and days after the first request are fine.
        await (await SubmitAsync(mine, Missed("MissedCheckOut", Yesterday(), "16:00"))).BodyAsync(HttpStatusCode.Created);
        await (await SubmitAsync(mine, Leave(Today().AddDays(8), Today().AddDays(8)))).BodyAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_employee_cancels_their_own_request_while_it_is_awaiting_review_and_it_stays_in_the_list()
    {
        var s = await SetupWorkAsync(factory);
        var (_, mine) = await AddLinkedEmployeeAsync(s);
        var (_, other) = await AddLinkedEmployeeAsync(s);
        var sent = await (await SubmitAsync(mine, Leave(Today().AddDays(2), Today().AddDays(2)))).BodyAsync(HttpStatusCode.Created);
        var id = sent.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Me}/leave-requests/{id}")).StatusCode);

        var cancelled = await (await mine.DeleteAsync($"{Me}/leave-requests/{id}")).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(("Cancelled", StatusIds.EmployeeRequest.Cancelled), (cancelled.Str("status"), cancelled.GetProperty("statusId").GetInt32()));
        Assert.NotNull(cancelled.Str("cancelledAt"));
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("approved").ValueKind);
        // Cancelling again changes nothing; the Owner can no longer review it.
        Assert.Equal(HttpStatusCode.OK, (await mine.DeleteAsync($"{Me}/leave-requests/{id}")).StatusCode);
        var review = await ReviewAsync(s.Owner, cancelled, true);
        Assert.Equal(("MSG-GEN-09", HttpStatusCode.Conflict), (await review.ErrorCodeAsync(), review.StatusCode));
        // A new request for the same day is allowed once the first is cancelled.
        await (await SubmitAsync(mine, Leave(Today().AddDays(2), Today().AddDays(2)))).BodyAsync(HttpStatusCode.Created);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "EmployeeRequest.StatusChanged" && e.RecordId == id)));
    }

    [Fact]
    public async Task A_reviewed_request_cannot_be_cancelled()
    {
        var s = await SetupWorkAsync(factory);
        var (_, mine) = await AddLinkedEmployeeAsync(s);
        var approved = await (await SubmitAsync(mine, Leave(Today().AddDays(2), Today().AddDays(2)))).BodyAsync(HttpStatusCode.Created);
        var rejected = await (await SubmitAsync(mine, Leave(Today().AddDays(4), Today().AddDays(4)))).BodyAsync(HttpStatusCode.Created);
        await (await ReviewAsync(s.Owner, approved, true)).BodyAsync(HttpStatusCode.OK);
        await (await ReviewAsync(s.Owner, rejected, false, "Bận mùa thu hoạch")).BodyAsync(HttpStatusCode.OK);

        var a = await mine.DeleteAsync($"{Me}/leave-requests/{approved.GetProperty("id").GetGuid()}");
        var r = await mine.DeleteAsync($"{Me}/leave-requests/{rejected.GetProperty("id").GetGuid()}");

        Assert.Equal(("MSG-LABOR-26", HttpStatusCode.Conflict), (await a.ErrorCodeAsync(), a.StatusCode));
        Assert.Contains("approved", (await ProblemAsync(a)).Detail());
        Assert.Contains("rejected", (await ProblemAsync(r)).Detail());
    }

    [Fact]
    public async Task The_owner_approves_or_rejects_and_a_rejection_needs_a_note()
    {
        var s = await SetupWorkAsync(factory);
        var (_, mine) = await AddLinkedEmployeeAsync(s);
        var one = await (await SubmitAsync(mine, Leave(Today().AddDays(2), Today().AddDays(3)))).BodyAsync(HttpStatusCode.Created);
        var two = await (await SubmitAsync(mine, Leave(Today().AddDays(6), Today().AddDays(6)))).BodyAsync(HttpStatusCode.Created);

        var noNote = await ReviewAsync(s.Owner, one, false, " ");
        Assert.Equal(("MSG-GEN-01", "Note"), (await noNote.ErrorCodeAsync(), await FieldAsync(noNote)));
        var noAnswer = await s.Owner.PostAsJsonAsync($"{Workforce}/leave-requests/{one.GetProperty("id").GetGuid()}/review", new { response = "x", rowVersion = one.Str("rowVersion") });
        Assert.Equal("Approved", await FieldAsync(noAnswer));

        var approved = await (await ReviewAsync(s.Owner, one, true, "Đồng ý")).BodyAsync(HttpStatusCode.OK);
        var rejected = await (await ReviewAsync(s.Owner, two, false, "Đang thu hoạch")).BodyAsync(HttpStatusCode.OK);

        Assert.Equal((true, "Approved", "Đồng ý"), (approved.GetProperty("approved").GetBoolean(), approved.Str("status"), approved.Str("response")));
        Assert.Equal((false, "Rejected", "Đang thu hoạch"), (rejected.GetProperty("approved").GetBoolean(), rejected.Str("status"), rejected.Str("response")));
        Assert.NotNull(approved.Str("reviewedAt"));
        var again = await ReviewAsync(s.Owner, approved, false, "Đổi ý");
        Assert.Equal(("MSG-GEN-09", HttpStatusCode.Conflict), (await again.ErrorCodeAsync(), again.StatusCode));
        var stored = await factory.QueryAsync(db => db.EmployeeRequests.SingleAsync(r => r.Id == one.GetProperty("id").GetGuid()));
        Assert.Equal((StatusIds.EmployeeRequest.Approved, "Đồng ý"), (stored.StatusId, stored.ReviewNote));
        Assert.NotNull(stored.ReviewedByAccountId);
    }

    [Fact]
    public async Task The_list_shows_awaiting_requests_first_to_the_owner_and_the_manager_but_only_the_owner_decides()
    {
        await using var own = new AuthApiFactory();
        var s = await SetupWorkAsync(own);
        var (_, mine) = await AddLinkedEmployeeAsync(s);
        var old = await (await SubmitAsync(mine, Leave(Today().AddDays(2), Today().AddDays(2)))).BodyAsync(HttpStatusCode.Created);
        var decided = await (await SubmitAsync(mine, Leave(Today().AddDays(4), Today().AddDays(4)))).BodyAsync(HttpStatusCode.Created);
        var newer = await (await SubmitAsync(mine, Leave(Today().AddDays(6), Today().AddDays(6)))).BodyAsync(HttpStatusCode.Created);
        await (await ReviewAsync(s.Owner, decided, true)).BodyAsync(HttpStatusCode.OK);

        foreach (var viewer in new[] { s.Owner, s.Manager })
        {
            var list = (await viewer.GetFromJsonAsync<JsonElement>($"{Workforce}/leave-requests")).EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
            Assert.Equal([old.GetProperty("id").GetGuid(), newer.GetProperty("id").GetGuid(), decided.GetProperty("id").GetGuid()], list);
        }

        var filtered = await s.Owner.GetFromJsonAsync<JsonElement>($"{Workforce}/leave-requests?statusId={StatusIds.EmployeeRequest.Approved}");
        Assert.Equal([decided.GetProperty("id").GetGuid()], filtered.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal("Status", await FieldAsync(await s.Owner.GetAsync($"{Workforce}/leave-requests?statusId=1")));
        Assert.Equal("MSG-GEN-08", await (await s.Manager.PostAsJsonAsync($"{Workforce}/leave-requests/{old.GetProperty("id").GetGuid()}/review", new { approved = true, rowVersion = old.Str("rowVersion") })).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await (await ManagerAsync(own, null, StaffRole.Accountant)).GetAsync($"{Workforce}/leave-requests")).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await own.CreateClient().GetAsync($"{Workforce}/leave-requests")).StatusCode);
    }

    [Fact]
    public async Task A_request_for_a_day_of_a_finalized_payroll_is_refused_and_approval_waits_for_a_payroll_awaiting_finalization()
    {
        await using var own = new AuthApiFactory();
        var s = await SetupWorkAsync(own);
        var (_, mine) = await AddLinkedEmployeeAsync(s);
        var pending = await (await SubmitAsync(mine, Leave(Today().AddDays(1), Today().AddDays(1)))).BodyAsync(HttpStatusCode.Created);
        var awaiting = await own.AddPayrollAsync(Day(Today().AddDays(-30)), Day(Today().AddDays(30)), StatusIds.Payroll.AwaitingFinalization);

        // MSG-LABOR-16: the Owner can still reject it with a note.
        var cannot = await ReviewAsync(s.Owner, pending, true);
        Assert.Equal(("MSG-LABOR-16", HttpStatusCode.Conflict), (await cannot.ErrorCodeAsync(), cannot.StatusCode));
        Assert.Contains("the payroll of", (await ProblemAsync(cannot)).Detail());
        await (await ReviewAsync(s.Owner, pending, false, "Chờ chốt lương")).BodyAsync(HttpStatusCode.OK);

        // A request for a day of a payroll Awaiting finalization is sent; one of a Finalized payroll is not.
        await (await SubmitAsync(mine, Leave(Today(), Today()))).BodyAsync(HttpStatusCode.Created);
        await own.UpdateAsync<OMTS.Domain.Entities.Labor.Payroll>(awaiting.Id, p => p.StatusId = StatusIds.Payroll.Finalized);
        var refused = await SubmitAsync(mine, Leave(Today().AddDays(-2), Today().AddDays(-1)));
        Assert.Equal(("MSG-LABOR-25", HttpStatusCode.Conflict), (await refused.ErrorCodeAsync(), refused.StatusCode));
        var afterPeriod = await SubmitAsync(mine, Leave(Today().AddDays(40), Today().AddDays(41)));
        Assert.Equal(HttpStatusCode.Created, afterPeriod.StatusCode);
    }

    [Fact]
    public async Task Approving_a_missed_check_in_records_the_requested_time_with_the_method_Request_and_works_out_the_hours()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        await AddPastDayAsync(s, employee, Yesterday(), null, "16:00", (s.Harvest, s.Raft.Id, null));
        var request = await (await SubmitAsync(mine, Missed("MissedCheckIn", Yesterday(), "06:30"))).BodyAsync(HttpStatusCode.Created);

        var approved = await (await ReviewAsync(s.Owner, request, true, "Đã kiểm tra")).BodyAsync(HttpStatusCode.OK);

        Assert.True(approved.GetProperty("approved").GetBoolean());
        var record = await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee));
        Assert.Equal(DateTimeOffset.Parse(Time(Yesterday(), "06:30")), record.CheckInAt);
        Assert.Equal((AttendanceMethod.Request, AttendanceMethod.Manual, "Quên chấm công"), (record.CheckInMethod, record.CheckOutMethod, record.CheckInReason));
        var day = await s.Owner.GetFromJsonAsync<JsonElement>($"{Workforce}/attendance/{employee}/{Day(Yesterday())}");
        Assert.True(day.GetProperty("complete").GetBoolean());
        Assert.Equal(9.5m, day.GetProperty("jobs")[0].GetProperty("hours").GetDecimal());
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Attendance.AppliedFromRequest" && e.RecordId == record.Id)));
    }

    [Fact]
    public async Task Approving_a_missed_check_out_with_no_attendance_yet_leaves_the_day_incomplete()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        await AddPastDayAsync(s, employee, Yesterday(), null, null, (s.Harvest, s.Raft.Id, null));
        var request = await (await SubmitAsync(mine, Missed("MissedCheckOut", Yesterday(), "16:00"))).BodyAsync(HttpStatusCode.Created);

        await (await ReviewAsync(s.Owner, request, true)).BodyAsync(HttpStatusCode.OK);

        var record = await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee));
        Assert.Equal((null, AttendanceMethod.Request), (record.CheckInAt, record.CheckOutMethod));
        Assert.False(record.IsComplete);
    }

    [Fact]
    public async Task A_requested_time_that_puts_the_times_out_of_order_cannot_be_approved()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        await AddPastDayAsync(s, employee, Yesterday(), "06:00", "16:00", (s.Harvest, s.Raft.Id, null), (s.Hanging, s.Raft.Id, "10:00"));
        var checkIn = await (await SubmitAsync(mine, Missed("MissedCheckIn", Yesterday(), "17:00"))).BodyAsync(HttpStatusCode.Created);
        var checkOut = await (await SubmitAsync(mine, Missed("MissedCheckOut", Yesterday(), "05:00"))).BodyAsync(HttpStatusCode.Created);
        var withoutRecord = await (await SubmitAsync(mine, Missed("MissedCheckOut", Yesterday().AddDays(-1), "05:00"))).BodyAsync(HttpStatusCode.Created);

        foreach (var request in new[] { checkIn, checkOut })
        {
            var response = await ReviewAsync(s.Owner, request, true);
            Assert.Equal(("MSG-LABOR-16", HttpStatusCode.Conflict), (await response.ErrorCodeAsync(), response.StatusCode));
        }

        // Nothing changed, and the requests are still awaiting review.
        var record = await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee));
        Assert.Equal((DateTimeOffset.Parse(Time(Yesterday(), "06:00")), DateTimeOffset.Parse(Time(Yesterday(), "16:00"))), (record.CheckInAt, record.CheckOutAt));
        Assert.Equal(3, await factory.QueryAsync(db => db.EmployeeRequests.CountAsync(r => r.EmployeeId == employee && r.StatusId == StatusIds.EmployeeRequest.AwaitingReview)));
        // A job period that would start before the new check-in is also in the way.
        var late = await (await SubmitAsync(mine, Missed("MissedCheckIn", Yesterday().AddDays(-2), "08:00"))).BodyAsync(HttpStatusCode.Created);
        await AddPastDayAsync(s, employee, Yesterday().AddDays(-2), null, "16:00", (s.Harvest, s.Raft.Id, null), (s.Hanging, s.Raft.Id, "07:00"));
        Assert.Equal("MSG-LABOR-16", await (await ReviewAsync(s.Owner, late, true)).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await ReviewAsync(s.Owner, withoutRecord, true)).StatusCode);
    }

    [Fact]
    public async Task A_seasonal_worker_with_no_work_that_day_gets_the_job_and_raft_the_owner_chooses()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s);
        var request = await (await SubmitAsync(mine, Missed("MissedCheckIn", Yesterday(), "06:30"))).BodyAsync(HttpStatusCode.Created);

        var missingJob = await ReviewAsync(s.Owner, request, true);
        Assert.Equal("Job type", await FieldAsync(missingJob));
        var missingRaft = await ReviewAsync(s.Owner, request, true, null, s.Harvest, null);
        Assert.Equal("Raft", await FieldAsync(missingRaft));
        Assert.False(await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(w => w.EmployeeId == employee)));

        await (await ReviewAsync(s.Owner, request, true, null, s.Harvest, s.Raft.Id)).BodyAsync(HttpStatusCode.OK);

        var assignment = await factory.QueryAsync(db => db.WorkAssignments.SingleAsync(w => w.EmployeeId == employee));
        Assert.Equal((s.Harvest, s.Raft.Id, Yesterday(), (DateTimeOffset?)null), (assignment.JobTypeId, assignment.RaftId, assignment.Date, assignment.StartsAt));
        Assert.Equal(DateTimeOffset.Parse(Time(Yesterday(), "06:30")), (await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee))).CheckInAt);
    }

    [Fact]
    public async Task A_staff_employee_needs_no_work_assignment_for_a_missed_check_in()
    {
        var s = await SetupWorkAsync(factory);
        var (employee, mine) = await AddLinkedEmployeeAsync(s, staff: true);
        var request = await (await SubmitAsync(mine, Missed("MissedCheckIn", Yesterday(), "07:00"))).BodyAsync(HttpStatusCode.Created);

        await (await ReviewAsync(s.Owner, request, true)).BodyAsync(HttpStatusCode.OK);

        Assert.False(await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(w => w.EmployeeId == employee)));
        Assert.NotNull((await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee))).CheckInAt);
    }
}
