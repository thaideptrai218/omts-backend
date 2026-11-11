using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.PayrollTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-12 and LABOR-13: making a payroll sheet from attendance, the rules for its period, reloading, finalizing and cancelling it.</summary>
public class PayrollSheetTests
{
    [Fact]
    public async Task A_sheet_is_made_provisional_with_a_payslip_per_worker_paid_hours_times_rate()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", FirstDays(3, 4));
        var binh = await WorkerAsync(s, "Bình", FirstDays(3, 2));
        await AddEmployeeAsync(s, "Không giờ");

        var sheet = await NewSheetAsync(s.Owner, 3);

        Assert.Equal(("Provisional", "Monthly", "All", "Bảng lương tháng 03/2024", "2024-03-01", "2024-03-31"),
            (sheet.Str("status"), sheet.Str("periodType"), sheet.Str("scope"), sheet.Str("name"), sheet.Str("from"), sheet.Str("to")));
        Assert.Matches(@"^BL\d{6}$", sheet.Str("code"));
        Assert.Equal(JsonValueKind.Null, sheet.GetProperty("expenseMonth").ValueKind);
        Assert.NotNull(sheet.Str("createdBy"));
        var slips = sheet.GetProperty("slips").EnumerateArray().ToList();
        Assert.Equal(["An", "Bình"], slips.Select(x => x.Str("employeeName")));
        var first = sheet.SlipOf(an);
        Assert.Matches(@"^PL\d{6}$", first.Str("code"));
        Assert.Matches(@"^NV\d{6}$", first.Str("employeeCode"));
        Assert.Equal((32m, 1_280_000m, 0m, 0m, 1_280_000m, 1_280_000m, 0m, 1_280_000m), (first.Money("hours"), first.Money("mainPay"), first.Money("bonus"), first.Money("deduction"), first.Money("income"), first.Money("net"), first.Money("paid"), first.Money("outstanding")));
        Assert.Equal("40.000", first.Str("hourlyRateNote"));
        Assert.Equal((16m, 640_000m), (sheet.SlipOf(binh).Money("hours"), sheet.SlipOf(binh).Money("mainPay")));
        Assert.Equal((1_920_000m, 0m, 1_920_000m), (sheet.Money("total"), sheet.Money("paid"), sheet.Money("outstanding")));
        Assert.Equal(1, await factory.AuditAsync("Payroll.Created"));
        Assert.Equal(sheet.Str("code"), (await s.Owner.SheetAsync(sheet.GuidOf())).Str("code"));
    }

    [Fact]
    public async Task The_hours_of_a_day_are_paid_at_the_rate_in_force_that_day()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var worker = await WorkerAsync(s, "Phúc", new DateOnly(2024, 4, 10), new DateOnly(2024, 4, 11));
        await s.Owner.PostRateAsync(await s.Owner.ReadJobTypeAsync(s.Harvest), 50_000, "2024-04-11");

        var slip = (await NewSheetAsync(s.Owner, 4, worker)).SlipOf(worker);

        Assert.Equal(8 * 40_000m + 8 * 50_000m, slip.Money("mainPay"));
        Assert.Equal("40.000 / 50.000", slip.Str("hourlyRateNote"));
        var detail = await s.Owner.JsonAsync($"/api/payroll-slips/{slip.GuidOf()}");
        Assert.Equal([(40_000m, 8m, 320_000m), (50_000m, 8m, 400_000m)],
            detail.GetProperty("lines").EnumerateArray().Select(l => (l.Money("hourlyRate"), l.Money("hours"), l.Money("amount"))));
        Assert.Equal([("2024-04-10", 8m), ("2024-04-11", 8m)], detail.GetProperty("attendance").EnumerateArray().Select(a => (a.Str("date")!, a.Money("hours"))));
    }

    [Fact]
    public async Task A_sheet_of_selected_employees_covers_only_them_and_a_sheet_without_hours_is_refused()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", FirstDays(5, 1));
        await WorkerAsync(s, "Bình", FirstDays(5, 1));
        var idle = await AddEmployeeAsync(s, "Nghỉ");

        var sheet = await NewSheetAsync(s.Owner, 5, an);
        var none = await CreateSheetAsync(s.Owner, MonthBody(6, an));
        var idleOnly = await CreateSheetAsync(s.Owner, MonthBody(5, idle));

        Assert.Equal(["An"], sheet.GetProperty("slips").EnumerateArray().Select(x => x.Str("employeeName")));
        Assert.Equal("Selected", sheet.Str("scope"));
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (none.StatusCode, await none.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.BadRequest, idleOnly.StatusCode);
    }

    [Fact]
    public async Task Invalid_periods_and_scopes_are_refused_with_the_field()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var worker = await WorkerAsync(s, "An", FirstDays(3, 1));
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));

        var cases = new (object Body, string Field)[]
        {
            (new { periodType = "Monthly", from = "2024-03-02", to = "2024-03-31", scope = "All" }, "Period"),
            (new { periodType = "Monthly", from = "2024-03-01", to = "2024-03-30", scope = "All" }, "Period"),
            (new { periodType = "Custom", from = "2024-03-10", to = "2024-03-01", scope = "All" }, "Period"),
            (new { periodType = "Custom", from = "2023-01-01", to = "2024-03-01", scope = "All" }, "Period"),
            (new { periodType = "Custom", from = Day(future), to = Day(future.AddDays(3)), scope = "All" }, "Period"),
            (new { periodType = "Custom", from = "2024-03-01", to = "2024-03-31", scope = "Selected" }, "Employees"),
            (new { periodType = "Custom", from = "2024-03-01", to = "2024-03-31", scope = "All", employeeIds = new[] { worker } }, "Employees"),
            (new { periodType = "Custom", from = "2024-03-01", to = "2024-03-31", scope = "Selected", employeeIds = new[] { Guid.NewGuid() } }, "Employees"),
            (new { periodType = "Weekly", from = "2024-03-01", to = "2024-03-31", scope = "All" }, "Period type"),
        };
        foreach (var (body, field) in cases)
        {
            var response = await CreateSheetAsync(s.Owner, body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        }

        var named = await CreateSheetAsync(s.Owner, cases[0].Body);
        Assert.Equal("Period", JsonDocument.Parse(await named.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
        Assert.Equal(0, await factory.QueryAsync(db => db.Payrolls.CountAsync()));
    }

    [Fact]
    public async Task The_same_period_for_the_same_worker_is_409_an_overlapping_one_is_400_and_a_cancelled_sheet_frees_the_period()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", FirstDays(3, 3));
        var binh = await WorkerAsync(s, "Bình", FirstDays(3, 3));
        var first = await NewSheetAsync(s.Owner, 3, an);

        var same = await CreateSheetAsync(s.Owner, MonthBody(3, an));
        var overlapping = await CreateSheetAsync(s.Owner, new { periodType = "Custom", from = "2024-03-02", to = "2024-03-20", scope = "Selected", employeeIds = new[] { an } });
        var allOverlap = await CreateSheetAsync(s.Owner, MonthBody(3));
        var other = await CreateSheetAsync(s.Owner, MonthBody(3, binh));

        Assert.Equal((HttpStatusCode.Conflict, "MSG-GEN-02"), (same.StatusCode, await same.ErrorCodeAsync()));
        Assert.Equal(first.GuidOf(), JsonDocument.Parse(await same.Content.ReadAsStringAsync()).RootElement.GetProperty("existingId").GetGuid());
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (overlapping.StatusCode, await overlapping.ErrorCodeAsync()));
        Assert.Equal(HttpStatusCode.Conflict, allOverlap.StatusCode);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);

        await s.Owner.PostAsync($"{Payrolls}/{first.GuidOf()}/cancel", null);
        Assert.Equal(HttpStatusCode.Created, (await CreateSheetAsync(s.Owner, MonthBody(3, an))).StatusCode);
    }

    [Fact]
    public async Task A_reload_follows_the_current_attendance_and_working_days_only_keeps_the_payslips()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", FirstDays(3, 1));
        var sheet = await NewSheetAsync(s.Owner, 3);
        var later = await WorkerAsync(s, "Bình", FirstDays(3, 2));
        await AddPastDayAsync(s, an, new DateOnly(2024, 3, 20), "07:00", "11:00", (s.Harvest, s.Raft.Id, null));
        var url = $"{Payrolls}/{sheet.GuidOf()}/reload";

        var hoursOnly = await (await s.Owner.PostAsJsonAsync(url, new { mode = "WorkingDaysOnly" })).BodyAsync(HttpStatusCode.OK);
        var all = await (await s.Owner.PostAsJsonAsync(url, new { mode = "All" })).BodyAsync(HttpStatusCode.OK);

        Assert.Equal([12m], hoursOnly.GetProperty("slips").EnumerateArray().Select(x => x.Money("hours")));
        Assert.Equal([12m, 16m], all.GetProperty("slips").EnumerateArray().Select(x => x.Money("hours")));
        Assert.Equal(16m, all.SlipOf(later).Money("hours"));
        Assert.Equal(2, await factory.AuditAsync("Payroll.Reloaded"));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync(url, new { mode = "Nothing" })).StatusCode);
    }

    [Fact]
    public async Task Finalizing_saves_the_month_locks_the_period_and_ends_every_change_of_the_sheet()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", FirstDays(3, 2));
        var sheet = await NewSheetAsync(s.Owner, 3, an);
        var id = sheet.GuidOf();

        var done = await (await FinalizeAsync(s.Owner, id, "2024-03")).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(("Finalized", "2024-03"), (done.Str("status"), done.Str("expenseMonth")));
        Assert.Equal(sheet.SlipOf(an).Str("code"), done.SlipOf(an).Str("code"));
        Assert.Equal(1, await factory.AuditAsync("Payroll.Finalized"));
        Assert.Equal(StatusIds.Payroll.Finalized, await factory.QueryAsync(db => db.Payrolls.Where(p => p.Id == id).Select(p => p.StatusId).SingleAsync()));
        Assert.Equal(HttpStatusCode.Conflict, (await FinalizeAsync(s.Owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsJsonAsync($"{Payrolls}/{id}/reload", new { mode = "All" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsync($"{Payrolls}/{id}/cancel", null)).StatusCode);

        // The records of the period are read-only (BR-LABOR-13, MSG-LABOR-04): the Owner cannot correct a day in it.
        var correction = await CorrectDayAsync(s, an, new DateOnly(2024, 3, 1));
        Assert.Equal((HttpStatusCode.Conflict, "MSG-LABOR-04"), (correction.StatusCode, await correction.ErrorCodeAsync()));
    }

    [Fact]
    public async Task A_sheet_that_is_not_ready_is_not_finalized_and_names_what_is_missing()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", FirstDays(3, 1));
        await AddPastDayAsync(s, an, new DateOnly(2024, 3, 5), "07:00", null, (s.Harvest, s.Raft.Id, null));
        var incomplete = await NewSheetAsync(s.Owner, 3, an);

        var refused = await FinalizeAsync(s.Owner, incomplete.GuidOf());

        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-LABOR-28"), (refused.StatusCode, problem.GetProperty("code").GetString()));
        Assert.Contains("incomplete", problem.Detail());
        Assert.Equal("Provisional", (await s.Owner.SheetAsync(incomplete.GuidOf())).Str("status"));

        var thisMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        await AddPastDayAsync(s, an, thisMonth, "07:00", "15:00", (s.Harvest, s.Raft.Id, null));
        var running = await s.Owner.PostAsJsonAsync(Payrolls, new { periodType = "Custom", from = Day(thisMonth), to = Day(thisMonth.AddMonths(1).AddDays(-1)), scope = "Selected", employeeIds = new[] { an } });
        var ended = await FinalizeAsync(s.Owner, (await running.BodyAsync(HttpStatusCode.Created)).GuidOf());
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-LABOR-28"), (ended.StatusCode, await ended.ErrorCodeAsync()));
    }

    [Fact]
    public async Task Hours_without_a_rate_and_requests_awaiting_review_stop_the_finalization()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var young = (await AddJobTypeAsync(s.Owner, $"Mới {NewCode()}", 30_000, "2024-06-01")).IdOf();
        var an = await WorkerAsync(s, "An", FirstDays(3, 1));
        await AddPastDayAsync(s, an, new DateOnly(2024, 3, 6), "07:00", "11:00", (young, s.Raft.Id, null));
        var sheet = await NewSheetAsync(s.Owner, 3, an);
        Assert.Equal(320_000m, sheet.SlipOf(an).Money("mainPay"));

        var noRate = await FinalizeAsync(s.Owner, sheet.GuidOf());

        Assert.Equal((HttpStatusCode.BadRequest, "MSG-LABOR-28"), (noRate.StatusCode, await noRate.ErrorCodeAsync()));

        // Without the day that has no rate, only the request of an employee stops it.
        await factory.UseDbAsync(db => db.Attendances.RemoveRange(db.Attendances.Where(a => a.Date == new DateOnly(2024, 3, 6))));
        await factory.UseDbAsync(db => db.WorkAssignments.RemoveRange(db.WorkAssignments.Where(a => a.Date == new DateOnly(2024, 3, 6))));
        await factory.UseDbAsync(db => db.EmployeeRequests.Add(new EmployeeRequest
        {
            EmployeeId = an, Type = OMTS.Domain.Enums.EmployeeRequestType.Leave, FromDate = new DateOnly(2024, 3, 10), ToDate = new DateOnly(2024, 3, 11),
            Reason = "Việc nhà", SentAt = DateTimeOffset.UtcNow, StatusId = StatusIds.EmployeeRequest.AwaitingReview,
        }));
        var waiting = await FinalizeAsync(s.Owner, sheet.GuidOf());
        Assert.Equal((HttpStatusCode.Conflict, "MSG-LABOR-31"), (waiting.StatusCode, await waiting.ErrorCodeAsync()));
    }

    [Fact]
    public async Task A_provisional_sheet_is_cancelled_and_a_cancelled_sheet_is_final()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", FirstDays(3, 1));
        var id = (await NewSheetAsync(s.Owner, 3, an)).GuidOf();

        var cancelled = await (await s.Owner.PostAsync($"{Payrolls}/{id}/cancel", null)).BodyAsync(HttpStatusCode.OK);

        Assert.Equal("Cancelled", cancelled.Str("status"));
        Assert.Equal(1, await factory.AuditAsync("Payroll.Cancelled"));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Owner.PostAsync($"{Payrolls}/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await FinalizeAsync(s.Owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.PostAsync($"{Payrolls}/{Guid.NewGuid()}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.GetAsync($"{Payrolls}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_provisional_sheet_does_not_lock_the_records_of_its_period_and_a_cancelled_one_is_never_touched()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var an = await WorkerAsync(s, "An", FirstDays(3, 1));
        var sheet = await NewSheetAsync(s.Owner, 3, an);

        var edit = await CorrectDayAsync(s, an, new DateOnly(2024, 3, 1));

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var reloaded = await (await s.Owner.PostAsJsonAsync($"{Payrolls}/{sheet.GuidOf()}/reload", new { mode = "All" })).BodyAsync(HttpStatusCode.OK);
        Assert.Equal(9m, reloaded.SlipOf(an).Money("hours"));
    }

    [Fact]
    public async Task The_sheet_detail_names_its_creator_and_counts_its_payslips()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        await WorkerAsync(s, "An", FirstDays(3, 2));
        await WorkerAsync(s, "Bình", FirstDays(3, 1));
        var made = await NewSheetAsync(s.Owner, 3);

        var sheet = await s.Owner.SheetAsync(made.GuidOf());

        Assert.Equal(2, sheet.GetProperty("employeeCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(sheet.Str("createdByName")));
        Assert.Equal(sheet.Str("createdBy"), sheet.Str("createdByName"));
        Assert.Equal(JsonValueKind.Null, sheet.GetProperty("expenseMonth").ValueKind);
        Assert.Equal(JsonValueKind.Null, sheet.GetProperty("note").ValueKind);
        Assert.True(DateTimeOffset.TryParse(sheet.Str("createdAt"), out _));
    }
}
