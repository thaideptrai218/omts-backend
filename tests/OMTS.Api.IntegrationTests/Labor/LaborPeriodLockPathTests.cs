using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.LaborCost;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-08 period lock on every path not covered elsewhere: moving work into or out of a finalized month, editing saved
/// attendance, evidence uploads, re-confirming piecework out of a finalized month, and reopening that unlocks them all.
/// </summary>
public class LaborPeriodLockPathTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private static int _pairs;

    private static (PayrollPeriod First, PayrollPeriod Next) ConsecutiveMonths()
    {
        var start = new DateOnly(2006, 1, 1).AddMonths(2 * (Interlocked.Increment(ref _pairs) - 1));
        var next = start.AddMonths(1);
        return (new PayrollPeriod(start.Year, start.Month), new PayrollPeriod(next.Year, next.Month));
    }

    private static MultipartFormDataContent Photo()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "proof.png");
        return form;
    }

    private static string SheetOf(LaborWorld w, DateOnly date) => $"{w.Attendance}/{date:yyyy-MM-dd}";

    [Fact]
    public async Task Moving_an_assignment_into_or_out_of_a_finalized_month_is_locked_and_changes_nothing()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var open = NewMonth();
        var locked = NewMonth();
        object Daily(DateOnly date, string? rowVersion = null) => new { workDate = date, workerId = worker.Id, task = "Trực bè", paymentType = "DailyWage", rowVersion };
        var inOpen = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(open.Day(5))), HttpStatusCode.Created);
        var inLocked = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(locked.Day(5))), HttpStatusCode.Created);
        await w.FinalizeMonthAsync(locked);

        var moveIn = await (await w.Owner.PutAsJsonAsync($"{w.Assignments}/{inOpen.GetProperty("id").GetGuid()}",
            Daily(locked.Day(6), inOpen.GetProperty("rowVersion").GetString()))).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        var moveOut = await (await w.Owner.PutAsJsonAsync($"{w.Assignments}/{inLocked.GetProperty("id").GetGuid()}",
            Daily(open.Day(6), inLocked.GetProperty("rowVersion").GetString()))).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        var cancel = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{inLocked.GetProperty("id").GetGuid()}/cancel", new { reason = "Mưa" }))
            .ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");

        Assert.Equal((locked.Key, locked.Key, locked.Key),
            (moveIn.GetProperty("period").GetString(), moveOut.GetProperty("period").GetString(), cancel.GetProperty("period").GetString()));
        var stillOpen = await ExpectAsync(await w.Owner.GetAsync($"{w.Assignments}/{inOpen.GetProperty("id").GetGuid()}"), HttpStatusCode.OK);
        var stillLocked = await ExpectAsync(await w.Owner.GetAsync($"{w.Assignments}/{inLocked.GetProperty("id").GetGuid()}"), HttpStatusCode.OK);
        Assert.Equal((open.Day(5).ToString("yyyy-MM-dd"), locked.Day(5).ToString("yyyy-MM-dd"), "Active"),
            (stillOpen.GetProperty("workDate").GetString(), stillLocked.GetProperty("workDate").GetString(), stillLocked.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Saved_attendance_of_a_finalized_month_can_neither_be_edited_nor_get_evidence()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var month = NewMonth();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(8)), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);
        var recordId = await factory.QueryAsync(db => db.AttendanceRecords.Where(r => r.WorkerId == worker.Id).Select(r => r.Id).SingleAsync());
        await w.FinalizeMonthAsync(month);

        await (await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(8)), new { rows = new[] { new { workerId = worker.Id, result = "UnpaidLeave", reason = "Nghỉ không phép" } } }))
            .ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        await (await w.Owner.PostAsync($"{w.Attendance}/records/{recordId}/evidence", Photo())).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");

        var record = await factory.QueryAsync(db => db.AttendanceRecords.AsNoTracking().SingleAsync(r => r.Id == recordId));
        Assert.Equal((AttendanceResult.Present, 0), (record.Result, record.EvidenceUrls.Count));
    }

    [Fact]
    public async Task Confirmed_piecework_cannot_be_moved_out_of_a_finalized_month_nor_get_evidence()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var (locked, open) = ConsecutiveMonths();
        var piece = await w.AddPieceworkAsync(locked.Day(10), null, worker.Id, null, completed: 4);
        await w.FinalizeMonthAsync(locked);

        var moved = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion",
            new { completedQuantity = 6, completionDate = open.Day(1), reason = "Đếm lại" })).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        await (await w.Owner.PostAsync($"{w.Assignments}/{piece.Id}/completion/evidence", Photo())).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");

        Assert.Equal(locked.Key, moved.GetProperty("period").GetString());
        var saved = await factory.QueryAsync(db => db.WorkAssignments.AsNoTracking().SingleAsync(a => a.Id == piece.Id));
        Assert.Equal((4m, locked.Day(10), 0), (saved.CompletedQuantity, saved.CompletionDate, saved.CompletionEvidenceUrls.Count));
    }

    [Fact]
    public async Task Reopening_unlocks_attendance_edits_completion_and_refresh_until_the_month_is_finalized_again()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var month = NewMonth();
        await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 300_000);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(3)), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);
        var piece = await w.AddPieceworkAsync(month.Day(4), null, worker.Id, null);
        await w.FinalizeMonthAsync(month);
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/reopen", new { reason = "Thiếu khoán" }), HttpStatusCode.OK);

        var rowVersion = await w.AttendanceVersionAsync(month.Day(3), worker.Id);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(3)),
            new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Nghỉ phép có lương", rowVersion } } }), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"{w.Assignments}/{piece.Id}/completion", new { completedQuantity = 37, completionDate = month.Day(5) }), HttpStatusCode.OK);
        var refreshed = await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/refresh", null), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var relocked = await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(3)), new { rows = new[] { new { workerId = worker.Id, result = "Present", reason = "x" } } });

        // 300,000 for the paid leave day + 37 × 2,500 = 92,500 → 93,000.
        Assert.Equal(393_000m, refreshed.PayslipOf(worker.Id).GetProperty("net").GetDecimal());
        await relocked.ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
    }
}
