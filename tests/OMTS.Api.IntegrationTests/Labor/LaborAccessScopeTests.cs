using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB access rules: zone managers only reach records of granted zones (also by record id), Accountant rights count only
/// at All zones, LAB-10 shows own finalized data only (never a reopened draft or another person's worker record), and the
/// pickers list Employed employees of the zone without personal data.
/// </summary>
public class LaborAccessScopeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    private static MultipartFormDataContent Photo()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files", "proof.png");
        return form;
    }

    [Fact]
    public async Task Zone_manager_cannot_reach_work_or_attendance_of_another_zone_by_record_id()
    {
        var w = await factory.WorldAsync();
        var otherZone = await factory.AddZoneAsync(NewCode());
        var worker = await w.AddWorkerAsync();
        var (manager, _) = await factory.StaffAsync(w.Zone,
            (PermissionFunctions.WorkAssignments, true, true, true, true), (PermissionFunctions.Attendance, true, true, true, false));
        var date = NewMonth().Day(9);
        var foreign = await w.AddPieceworkAsync(date, null, worker.Id, null);
        await factory.UpdateAsync<WorkAssignment>(foreign.Id, a => a.ZoneId = otherZone.Id);
        var foreignRecord = new AttendanceRecord
        {
            ZoneId = otherZone.Id, WorkerId = worker.Id, WorkDate = date, Result = AttendanceResult.Present, RecordedByAccountId = w.OwnerAccount.Id,
        };
        await factory.UseDbAsync(db => db.AttendanceRecords.Add(foreignRecord));
        var own = $"{w.Assignments}/{foreign.Id}";
        var theirs = $"/api/zones/{otherZone.Id}/work-assignments/{foreign.Id}";

        await (await manager.PutAsJsonAsync(own, new { workDate = date, workerId = worker.Id, task = "x", paymentType = "DailyWage", rowVersion = "" }))
            .ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.PostAsJsonAsync($"{own}/cancel", new { reason = "x" })).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.PostAsJsonAsync($"{own}/completion", new { completedQuantity = 3, completionDate = date })).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.PostAsync($"{own}/completion/evidence", Photo())).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.PostAsync($"{w.Attendance}/records/{foreignRecord.Id}/evidence", Photo())).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.GetAsync(theirs)).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await manager.PostAsJsonAsync($"{theirs}/cancel", new { reason = "x" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await manager.PostAsJsonAsync($"{theirs}/completion", new { completedQuantity = 3, completionDate = date })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await manager.PutAsJsonAsync($"/api/zones/{otherZone.Id}/attendance/{date:yyyy-MM-dd}",
            new { rows = new[] { new { workerId = worker.Id, result = "UnpaidLeave", reason = "x" } } })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");

        var untouched = await factory.QueryAsync(db => db.WorkAssignments.AsNoTracking().SingleAsync(a => a.Id == foreign.Id));
        Assert.Null(untouched.CancelledAt);
        Assert.Null(untouched.CompletionConfirmedAt);
        Assert.Equal(AttendanceResult.Present, await factory.QueryAsync(db => db.AttendanceRecords.Where(r => r.Id == foreignRecord.Id).Select(r => r.Result).SingleAsync()));
    }

    [Fact]
    public async Task Accountant_rights_count_only_when_granted_at_all_zones_and_zone_managers_never_reach_pay()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var month = NewMonth();
        var (zoneAccountant, _) = await factory.StaffAsync(w.Zone,
            (PermissionFunctions.PayRules, true, true, true, false), (PermissionFunctions.Payroll, true, true, true, false));
        var (allZonesAccountant, _) = await factory.StaffAsync(null,
            (PermissionFunctions.PayRules, true, true, true, false), (PermissionFunctions.Payroll, true, true, true, false));
        var (manager, _) = await factory.StaffAsync(w.Zone,
            (PermissionFunctions.LaborWorkers, true, true, true, false), (PermissionFunctions.WorkAssignments, true, true, true, true),
            (PermissionFunctions.Attendance, true, true, true, false));
        var rate = new { workerId = worker.Id, salaryType = "DailyWage", amount = 300_000, validFrom = month.Start, validTo = month.End };
        var advance = new { workerId = worker.Id, amount = 100_000, paymentDate = month.Day(2), method = "Cash" };

        foreach (var client in new[] { zoneAccountant, manager })
        {
            await (await client.GetAsync("/api/payrolls")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await client.PostAsJsonAsync("/api/payrolls", new { period = month.Key })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await client.GetAsync("/api/payslips")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await client.GetAsync("/api/salary-advances")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await client.PostAsJsonAsync("/api/salary-advances", advance)).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await client.GetAsync("/api/pay-rates/people")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await client.PostAsJsonAsync("/api/pay-rates", rate)).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await client.PostAsJsonAsync("/api/pay-rates/bulk", new { people = new[] { new { workerId = worker.Id } }, rate })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
            await (await client.PostAsync($"/api/payrolls/{month.Key}/finalize", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        }

        // The zone-granted Accountant cannot read workers either (PAYROLL View counts only at All zones).
        await (await zoneAccountant.GetAsync("/api/labor/workers")).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await ExpectAsync(await allZonesAccountant.PostAsJsonAsync("/api/pay-rates", rate), HttpStatusCode.Created);
        await ExpectAsync(await allZonesAccountant.PostAsJsonAsync("/api/salary-advances", advance), HttpStatusCode.Created);
        await ExpectAsync(await allZonesAccountant.PostAsJsonAsync("/api/payrolls", new { period = month.Key }), HttpStatusCode.Created);
        await ExpectAsync(await allZonesAccountant.GetAsync($"/api/labor/workers/{worker.Id}"), HttpStatusCode.OK);
        await (await allZonesAccountant.PostAsync($"/api/payrolls/{month.Key}/finalize", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
    }

    [Fact]
    public async Task Reopened_payslips_disappear_from_personal_pay_until_finalized_again()
    {
        var w = await factory.WorldAsync();
        var (me, client, _) = await w.AddEmployeeWithAccountAsync();
        var month = NewMonth();
        await w.AddRateAsync(month, me.Id, null, SalaryType.DailyWage, 300_000);
        await w.AddAttendanceAsync(month.Day(3), me.Id, null);
        var payslip = (await w.CreatePayrollAsync(month)).PayslipOf(me.Id).GetProperty("id").GetGuid();
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var visible = await ExpectAsync(await client.GetAsync($"/api/me/pay/payslips?year={month.Year}"), HttpStatusCode.OK);

        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/reopen", new { reason = "Sai công" }), HttpStatusCode.OK);
        var hidden = await ExpectAsync(await client.GetAsync($"/api/me/pay/payslips?year={month.Year}"), HttpStatusCode.OK);
        var detail = await client.GetAsync($"/api/me/pay/payslips/{payslip}");
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var again = await ExpectAsync(await client.GetAsync($"/api/me/pay/payslips/{payslip}"), HttpStatusCode.OK);

        Assert.Contains(visible.GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == payslip);
        Assert.DoesNotContain(hidden.GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == payslip);
        await detail.ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(300_000m, again.GetProperty("net").GetDecimal());
    }

    [Fact]
    public async Task Personal_pay_shows_the_caller_s_own_linked_worker_records_but_never_a_colleague_s()
    {
        var w = await factory.WorldAsync();
        var (me, client, _) = await w.AddEmployeeWithAccountAsync();
        var colleague = await w.AddEmployeeAsync();
        var mine = await w.AddWorkerAsync(configure: x => x.EmployeeId = me.Id);
        var theirs = await w.AddWorkerAsync(configure: x => x.EmployeeId = colleague.Id);
        var month = NewMonth();
        foreach (var worker in new[] { mine, theirs })
        {
            await w.AddRateAsync(month, null, worker.Id, SalaryType.DailyWage, 250_000);
            await w.AddAttendanceAsync(month.Day(6), null, worker.Id);
        }

        var payroll = await w.CreatePayrollAsync(month);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);

        var list = await ExpectAsync(await client.GetAsync($"/api/me/pay/payslips?year={month.Year}"), HttpStatusCode.OK);
        var other = await client.GetAsync($"/api/me/pay/payslips/{payroll.PayslipOf(theirs.Id).GetProperty("id").GetGuid()}");
        var attendance = await ExpectAsync(await client.GetAsync($"/api/me/pay/attendance?from={month.Start:yyyy-MM-dd}&to={month.End:yyyy-MM-dd}"), HttpStatusCode.OK);
        var tooLong = await client.GetAsync($"/api/me/pay/attendance?from={month.Start:yyyy-MM-dd}&to={month.Start.AddDays(400):yyyy-MM-dd}");

        Assert.Equal([payroll.PayslipOf(mine.Id).GetProperty("id").GetGuid()], list.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        await other.ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Single(attendance.GetProperty("items").EnumerateArray());
        await tooLong.ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
    }

    [Fact]
    public async Task Pickers_list_employed_employees_of_the_zone_and_in_use_workers_without_personal_data()
    {
        var w = await factory.WorldAsync();
        var otherZone = await factory.AddZoneAsync(NewCode());
        var tag = NewCode();
        var here = await w.AddEmployeeAsync($"Nhân viên {tag} A");
        var elsewhere = await w.AddEmployeeAsync($"Nhân viên {tag} B", inZone: false);
        await factory.UseDbAsync(db => db.EmployeeWorkingZones.Add(new OMTS.Domain.Entities.Identity.EmployeeWorkingZone { EmployeeId = elsewhere.Id, ZoneId = otherZone.Id }));
        var left = await w.AddEmployeeAsync($"Nhân viên {tag} C", e => (e.StatusId, e.EmploymentEndDate) = (StatusIds.Employee.Left, new DateOnly(2026, 1, 31)));
        var worker = await w.AddWorkerAsync(configure: x => (x.FullName, x.CitizenId) = ($"Thời vụ {tag}", "079123456789"));
        var discontinued = await w.AddWorkerAsync(configure: x => (x.FullName, x.StatusId) = ($"Thời vụ {tag} nghỉ", StatusIds.Usage.Discontinued));
        var (attendanceManager, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.Attendance, true, false, false, false));
        var (workManager, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.WorkAssignments, true, false, false, false));

        foreach (var response in new[]
        {
            await attendanceManager.GetAsync($"{w.Attendance}/people?search={Uri.EscapeDataString(tag)}"),
            await workManager.GetAsync($"{w.Assignments}/assignees?search={Uri.EscapeDataString(tag)}"),
        })
        {
            var ids = (await ExpectAsync(response, HttpStatusCode.OK)).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(new[] { here.Id, worker.Id }.Order(), ids.Order());
            Assert.DoesNotContain(elsewhere.Id, ids);
            Assert.DoesNotContain(left.Id, ids);
            Assert.DoesNotContain(discontinued.Id, ids);
            Assert.DoesNotContain(worker.PhoneNumber!, text);
            Assert.DoesNotContain("079123456789", text);
            Assert.DoesNotContain(here.PhoneNumber!, text);
            Assert.DoesNotContain("citizenId", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
