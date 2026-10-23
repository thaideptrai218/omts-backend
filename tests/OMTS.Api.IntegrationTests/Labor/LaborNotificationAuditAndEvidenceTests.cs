using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-03 notifications only for employees with an Active account; one audit record per create, update, cancel and status
/// change across LAB-02…LAB-09 (with actor and reason); evidence files limited to JPEG/PNG, 5 MB each and 5 per record.
/// </summary>
public class LaborNotificationAuditAndEvidenceTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1];

    private static MultipartFormDataContent Files(params (byte[] Header, int Length, string Type)[] files)
    {
        var form = new MultipartFormDataContent();
        for (var i = 0; i < files.Length; i++)
        {
            var bytes = new byte[files[i].Length];
            files[i].Header.CopyTo(bytes, 0);
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(files[i].Type);
            form.Add(file, "files", $"evidence{i}.bin");
        }

        return form;
    }

    private static (byte[], int, string) Png(int length = 64) => (PngHeader, length, "image/png");

    private static string SheetOf(LaborWorld w, DateOnly date) => $"{w.Attendance}/{date:yyyy-MM-dd}";

    private async Task<List<string>> ActionsAsync(Guid recordId, Guid? actor = null)
    {
        var events = await factory.AuditOfAsync(recordId);
        if (actor is { } id)
        {
            Assert.All(events, e => Assert.Equal(id, e.ActorAccountId));
        }

        return events.Select(e => e.Action).ToList();
    }

    private Task<List<AuditEvent>> AuditOfPeriodAsync(PayrollPeriod month) =>
        factory.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordCode == month.Key).OrderBy(e => e.OccurredAt).ToListAsync());

    [Fact]
    public async Task Only_employees_with_an_active_account_get_work_notifications_and_pushes()
    {
        var w = await factory.WorldAsync();
        var lockedAccount = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.StatusId = StatusIds.Account.Locked);
        var locked = await w.AddEmployeeAsync(configure: e => e.AccountId = lockedAccount.Id);
        var noAccount = await w.AddEmployeeAsync();
        var (active, _, activeAccount) = await w.AddEmployeeWithAccountAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(14);
        object Daily(Guid? employeeId, Guid? workerId = null, string? rowVersion = null) =>
            new { workDate = date, employeeId, workerId, task = "Trực bè", paymentType = "DailyWage", rowVersion, confirmDuplicate = true };

        foreach (var employee in new[] { locked, noAccount })
        {
            await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(employee.Id)), HttpStatusCode.Created);
        }

        var assigned = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(active.Id)), HttpStatusCode.Created);
        await ExpectAsync(await w.Owner.PutAsJsonAsync($"{w.Assignments}/{assigned.GetProperty("id").GetGuid()}",
            Daily(null, worker.Id, assigned.GetProperty("rowVersion").GetString())), HttpStatusCode.OK);

        var notices = await factory.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WorkAssignment && (n.RecipientAccountId == lockedAccount.Id || n.RecipientAccountId == activeAccount.Id))
            .OrderBy(n => n.CreatedAt).ToListAsync());
        var inbox = factory.Services.GetRequiredService<FakePushSender>().Inbox;
        Assert.DoesNotContain(notices, n => n.RecipientAccountId == lockedAccount.Id);
        Assert.Empty(inbox.List(lockedAccount.Id.ToString()));
        Assert.Equal(["New work assignment", "Work assignment cancelled"], notices.Select(n => n.Title));
        Assert.Equal(2, inbox.List(activeAccount.Id.ToString()).Count);
    }

    [Fact]
    public async Task Workforce_and_work_changes_each_write_an_audit_record_of_the_acting_manager()
    {
        var w = await factory.WorldAsync();
        var (manager, account) = await factory.StaffAsync(w.Zone,
            (PermissionFunctions.LaborWorkers, true, true, true, false), (PermissionFunctions.WorkAssignments, true, true, true, true));
        var date = NewMonth().Day(10);
        var team = await ExpectAsync(await manager.PostAsJsonAsync("/api/labor/teams", new { name = $"Tổ Bắc {NewCode()}" }), HttpStatusCode.Created);
        var teamId = team.GetProperty("id").GetGuid();
        await ExpectAsync(await manager.PutAsJsonAsync($"/api/labor/teams/{teamId}", new
        {
            name = team.GetProperty("name").GetString(), otherContacts = "Zalo nhóm", rowVersion = team.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);
        await ExpectAsync(await manager.PostAsync($"/api/labor/teams/{teamId}/deactivate", null), HttpStatusCode.OK);
        await ExpectAsync(await manager.PostAsync($"/api/labor/teams/{teamId}/reactivate", null), HttpStatusCode.OK);
        var worker = await ExpectAsync(await manager.PostAsJsonAsync("/api/labor/workers", new { fullName = $"Ngô Văn {NewCode()}", citizenId = "0791" + Random.Shared.Next(10_000_000, 99_999_999) }), HttpStatusCode.Created);
        var workerId = worker.GetProperty("id").GetGuid();
        await ExpectAsync(await manager.PutAsJsonAsync($"/api/labor/workers/{workerId}", new
        {
            fullName = worker.GetProperty("fullName").GetString(), citizenId = worker.GetProperty("citizenId").GetString(), teamId,
            rowVersion = worker.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);
        var daily = await ExpectAsync(await manager.PostAsJsonAsync(w.Assignments, new { workDate = date, workerId, task = "Trực bè", paymentType = "DailyWage" }), HttpStatusCode.Created);
        var dailyId = daily.GetProperty("id").GetGuid();
        await ExpectAsync(await manager.PutAsJsonAsync($"{w.Assignments}/{dailyId}", new
        {
            workDate = date, workerId, task = "Trực bè đêm", paymentType = "DailyWage", rowVersion = daily.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);
        await ExpectAsync(await manager.PostAsJsonAsync($"{w.Assignments}/{dailyId}/cancel", new { reason = "Bão" }), HttpStatusCode.OK);
        var piece = await ExpectAsync(await manager.PostAsJsonAsync(w.Assignments, new
        {
            workDate = date, workerId, task = "Buộc dây", paymentType = "Piecework", pieceUnit = "rope", plannedQuantity = 40, pieceRate = 500, confirmDuplicate = true,
        }), HttpStatusCode.Created);
        var pieceId = piece.GetProperty("id").GetGuid();
        await ExpectAsync(await manager.PostAsJsonAsync($"{w.Assignments}/{pieceId}/completion", new { completedQuantity = 38, completionDate = date }), HttpStatusCode.OK);
        await ExpectAsync(await manager.PostAsync($"{w.Assignments}/{pieceId}/completion/evidence", Files(Png())), HttpStatusCode.OK);
        await ExpectAsync(await manager.PostAsync($"/api/labor/workers/{workerId}/deactivate", null), HttpStatusCode.OK);

        Assert.Equal(["Contractor.Created", "Contractor.Updated", "Contractor.StatusChanged", "Contractor.StatusChanged"], await ActionsAsync(teamId, account.Id));
        Assert.Equal(["SeasonalWorker.Created", "SeasonalWorker.Updated", "SeasonalWorker.StatusChanged"], await ActionsAsync(workerId, account.Id));
        Assert.Equal(["WorkAssignment.Created", "WorkAssignment.Updated", "WorkAssignment.Cancelled"], await ActionsAsync(dailyId, account.Id));
        Assert.Equal(["WorkAssignment.Created", "WorkAssignment.CompletionConfirmed", "WorkAssignment.EvidenceAdded"], await ActionsAsync(pieceId, account.Id));
        Assert.Equal("Bão", (await factory.AuditOfAsync(dailyId)).Single(e => e.Action == "WorkAssignment.Cancelled").Reason);
    }

    [Fact]
    public async Task Attendance_pay_rate_payroll_payment_and_advance_changes_each_write_an_audit_record()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var worker = await w.AddWorkerAsync();
        var (accountant, accountantAccount) = await factory.StaffAsync(null,
            (PermissionFunctions.PayRules, true, true, true, false), (PermissionFunctions.Payroll, true, true, true, false));
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(3)), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);
        var rowVersion = await w.AttendanceVersionAsync(month.Day(3), worker.Id);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(3)), new { rows = new[] { new { workerId = worker.Id, result = "PaidLeave", reason = "Ốm", rowVersion } } }), HttpStatusCode.OK);
        var recordId = await factory.QueryAsync(db => db.AttendanceRecords.Where(r => r.WorkerId == worker.Id).Select(r => r.Id).SingleAsync());
        await ExpectAsync(await w.Owner.PostAsync($"{w.Attendance}/records/{recordId}/evidence", Files(Png())), HttpStatusCode.OK);
        var first = await ExpectAsync(await accountant.PostAsJsonAsync("/api/pay-rates",
            new { workerId = worker.Id, salaryType = "DailyWage", amount = 300_000, validFrom = month.Start }), HttpStatusCode.Created);
        var second = await ExpectAsync(await accountant.PostAsJsonAsync("/api/pay-rates",
            new { workerId = worker.Id, salaryType = "DailyWage", amount = 320_000, validFrom = month.Day(20), validTo = month.End }), HttpStatusCode.Created);
        var (firstId, secondId) = (first.GetProperty("rate").GetProperty("id").GetGuid(), second.GetProperty("rate").GetProperty("id").GetGuid());
        await ExpectAsync(await accountant.PutAsJsonAsync($"/api/pay-rates/{secondId}", new
        {
            salaryType = "DailyWage", amount = 330_000, validFrom = month.Day(20), validTo = month.End, reason = "Nhập sai",
            rowVersion = second.GetProperty("rate").GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);
        var payslip = (await w.CreatePayrollAsync(month, accountant)).PayslipOf(worker.Id).GetProperty("id").GetGuid();
        await ExpectAsync(await accountant.PostAsJsonAsync($"/api/payrolls/{month.Key}/payslips/{payslip}/lines",
            new { type = "Addition", amount = 100_000, reason = "Thưởng" }), HttpStatusCode.Created);
        await ExpectAsync(await accountant.PostAsync($"/api/payrolls/{month.Key}/refresh", null), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/send-back", new { reason = "Xem lại thưởng" }), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payrolls/{month.Key}/reopen", new { reason = "Sai công" }), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        var wrong = await ExpectAsync(await accountant.PostAsJsonAsync($"/api/payslips/{payslip}/payments",
            new { amountPaid = 50_000, paymentDate = month.End, method = "Cash" }), HttpStatusCode.Created);
        var wrongId = wrong.GetProperty("payment").GetProperty("id").GetGuid();
        await ExpectAsync(await accountant.PostAsync($"/api/labor-payments/{wrongId}/documents", Files(Png())), HttpStatusCode.OK);
        await ExpectAsync(await accountant.PostAsJsonAsync($"/api/labor-payments/{wrongId}/reverse", new { reason = "Nhầm phiếu" }), HttpStatusCode.Created);
        await ExpectAsync(await accountant.PostAsJsonAsync($"/api/payslips/{payslip}/payments",
            new { amountPaid = 400_000, paymentDate = month.End, method = "BankTransfer" }), HttpStatusCode.Created);
        var advance = await ExpectAsync(await accountant.PostAsJsonAsync("/api/salary-advances",
            new { workerId = worker.Id, amount = 100_000, paymentDate = month.End, method = "Cash" }), HttpStatusCode.Created);
        var advanceId = advance.GetProperty("id").GetGuid();
        await ExpectAsync(await accountant.PostAsJsonAsync($"/api/labor-payments/{advanceId}/reverse", new { reason = "Ghi nhầm người" }), HttpStatusCode.Created);

        Assert.Equal(["AttendanceRecord.Created", "AttendanceRecord.Updated", "AttendanceRecord.EvidenceAdded"], await ActionsAsync(recordId, w.OwnerAccount.Id));
        Assert.Equal("Ốm", (await factory.AuditOfAsync(recordId)).Single(e => e.Action == "AttendanceRecord.Updated").Reason);
        Assert.Equal(["PayRuleVersion.Created", "PayRuleVersion.Closed"], await ActionsAsync(firstId, accountantAccount.Id));
        Assert.Equal(["PayRuleVersion.Created", "PayRuleVersion.Updated"], await ActionsAsync(secondId, accountantAccount.Id));
        Assert.Equal("Nhập sai", (await factory.AuditOfAsync(secondId)).Single(e => e.Action == "PayRuleVersion.Updated").Reason);
        var period = await AuditOfPeriodAsync(month);
        foreach (var action in new[] { "Payroll.Created", "PayLine.Created", "Payroll.Refreshed", "Payroll.SentBack", "Payroll.Reopened", "LaborPayment.Created" })
        {
            Assert.Contains(period, e => e.Action == action);
        }

        Assert.Equal(2, period.Count(e => e.Action == "Payroll.Finalized"));
        Assert.Equal(("Xem lại thưởng", "Sai công"),
            (period.Single(e => e.Action == "Payroll.SentBack").Reason, period.Single(e => e.Action == "Payroll.Reopened").Reason));
        var slipEvents = await factory.AuditOfAsync(payslip);
        // Draft → Approved, Approved → Draft (reason), Draft → Approved, Approved → Fully Paid.
        Assert.Equal(4, slipEvents.Count(e => e.Action == "LaborSettlement.StatusChanged"));
        Assert.Contains(slipEvents, e => e.Action == "LaborSettlement.StatusChanged" && e.Reason == "Sai công");
        Assert.Equal(["LaborPayment.Created", "LaborPayment.DocumentsAdded", "LaborPayment.Reversed"], await ActionsAsync(wrongId, accountantAccount.Id));
        Assert.Equal("Nhầm phiếu", (await factory.AuditOfAsync(wrongId)).Single(e => e.Action == "LaborPayment.Reversed").Reason);
        Assert.Equal(["SalaryAdvance.Created", "LaborPayment.Reversed"], await ActionsAsync(advanceId, accountantAccount.Id));
    }

    [Fact]
    public async Task Evidence_must_be_jpeg_or_png_of_at_most_five_megabytes_and_five_per_record()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(12);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, date), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);
        var recordId = await factory.QueryAsync(db => db.AttendanceRecords.Where(r => r.WorkerId == worker.Id).Select(r => r.Id).SingleAsync());
        var evidence = $"{w.Attendance}/records/{recordId}/evidence";
        var piece = await w.AddPieceworkAsync(date, null, worker.Id, null, completed: 3);
        const int FiveMegabytes = 5 * 1024 * 1024;

        var tooBig = await (await w.Owner.PostAsync(evidence, Files(Png(FiveMegabytes + 1)))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var pdf = await (await w.Owner.PostAsync(evidence, Files(("%PDF-1.7 "u8.ToArray(), 64, "image/png")))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var mixed = await (await w.Owner.PostAsync(evidence, Files(Png(), ("GIF89a"u8.ToArray(), 64, "image/gif")))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var exactly = await ExpectAsync(await w.Owner.PostAsync(evidence, Files((JpegHeader, FiveMegabytes, "image/jpeg"))), HttpStatusCode.OK);
        var four = await ExpectAsync(await w.Owner.PostAsync(evidence, Files(Png(), Png(), Png(), Png())), HttpStatusCode.OK);
        var sixth = await (await w.Owner.PostAsync(evidence, Files(Png()))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var bigCompletion = await (await w.Owner.PostAsync($"{w.Assignments}/{piece.Id}/completion/evidence", Files(Png(FiveMegabytes + 1))))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.All(new[] { tooBig, pdf, mixed, sixth, bigCompletion }, p => Assert.Equal("Evidence", p.Field()));
        Assert.Equal((1, 5), (exactly.GetProperty("evidenceUrls").GetArrayLength(), four.GetProperty("evidenceUrls").GetArrayLength()));
        // Nothing of a refused upload is kept: one JPEG and four PNGs only.
        Assert.Equal(5, await factory.QueryAsync(db => db.AttendanceRecords.Where(r => r.Id == recordId).Select(r => r.EvidenceUrls.Count).SingleAsync()));
    }
}
