using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// Set-up of the payroll tests (LABOR-12 to LABOR-14). Work is written for days in 2024, long before today and in a period with no break or rounding
/// settings, so a day of 07:00 to 15:00 is 8 hours at the 40,000 per hour of the test job types and pays 320,000. Each test has its own factory.
/// </summary>
internal static class PayrollTestKit
{
    public const string Payrolls = "/api/payrolls";
    public const string Payments = "/api/payroll-payments";
    public const decimal DayPay = 320_000;

    /// <summary>The first <paramref name="count"/> days of a month of 2024.</summary>
    public static DateOnly[] FirstDays(int month, int count) => Enumerable.Range(1, count).Select(d => new DateOnly(2024, month, d)).ToArray();

    public static DateOnly[] Dates(params string[] days) => days.Select(DateOnly.Parse).ToArray();

    public static string Last(int month) => Day(new DateOnly(2024, month, DateTime.DaysInMonth(2024, month)));

    /// <summary>An employee with a complete day of 8 hours of harvesting on each of the days.</summary>
    public static async Task<Guid> WorkerAsync(WorkSetup s, string name, params DateOnly[] days)
    {
        var employee = await AddEmployeeAsync(s, name);
        foreach (var day in days)
        {
            await AddPastDayAsync(s, employee, day, "07:00", "15:00", (s.Harvest, s.Raft.Id, null));
        }

        return employee;
    }

    public static object MonthBody(int month, params Guid[] employees) => new
    {
        periodType = "Monthly",
        from = Day(new DateOnly(2024, month, 1)),
        to = Last(month),
        scope = employees.Length == 0 ? "All" : "Selected",
        employeeIds = employees.Length == 0 ? null : employees,
    };

    public static Task<HttpResponseMessage> CreateSheetAsync(HttpClient client, object body) => client.PostAsJsonAsync(Payrolls, body);

    /// <summary>Makes a Provisional sheet of the month for the employees (every employee with hours when none is named); fails the test unless it is 201.</summary>
    public static async Task<JsonElement> NewSheetAsync(HttpClient client, int month, params Guid[] employees) =>
        await (await CreateSheetAsync(client, MonthBody(month, employees))).BodyAsync(HttpStatusCode.Created);

    public static Task<HttpResponseMessage> FinalizeAsync(HttpClient client, Guid id, string? expenseMonth = null) =>
        client.PostAsJsonAsync($"{Payrolls}/{id}/finalize", new { expenseMonth = expenseMonth ?? "2024-12" });

    public static async Task<JsonElement> FinalizedAsync(HttpClient client, JsonElement sheet) =>
        await (await FinalizeAsync(client, sheet.GetProperty("id").GetGuid())).BodyAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> PayAsync(HttpClient client, Guid sheetId, string method, params (Guid Slip, decimal Amount)[] lines) =>
        client.PostAsJsonAsync($"{Payrolls}/{sheetId}/payments", new
        {
            paidAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"),
            method,
            note = "Trả lương",
            lines = lines.Select(l => new { slipId = l.Slip, amount = l.Amount }).ToArray(),
        });

    public static Task<HttpResponseMessage> PayEmployeeAsync(HttpClient client, Guid employee, decimal amount, string method = "Cash") =>
        client.PostAsJsonAsync(Payments, new { employeeId = employee, amount, paidAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o"), method, note = "Tạm ứng" });

    /// <summary>The Owner corrects the check-in and check-out of a day to 8 hours of harvesting; the answer is the PUT's (200, or 409 MSG-LABOR-04 in a locked period).</summary>
    public static async Task<HttpResponseMessage> CorrectDayAsync(WorkSetup s, Guid employee, DateOnly day, string checkIn = "06:00", string checkOut = "15:00")
    {
        var url = $"{Workforce}/attendance/{employee}/{Day(day)}";
        var view = await s.Owner.JsonAsync(url);
        return await s.Owner.PutAsJsonAsync(url, new
        {
            checkInAt = Time(day, checkIn),
            checkOutAt = Time(day, checkOut),
            periods = new[] { new { jobTypeId = s.Harvest, raftId = s.Raft.Id, startsAt = (string?)null } },
            reason = "Sửa giờ",
            rowVersion = view.Str("rowVersion"),
        });
    }

    /// <summary>The in-memory test database may lack the seeded voucher types; the payroll type is added when it is missing.</summary>
    public static async Task EnsureVoucherTypeAsync(this AuthApiFactory factory)
    {
        if (await factory.QueryAsync(db => db.CashVoucherTypes.AnyAsync(t => t.Id == OMTS.Domain.Constants.CashVoucherTypeIds.PaymentPayroll)))
        {
            return;
        }

        await factory.UseDbAsync(db =>
        {
            var type = new OMTS.Domain.Entities.CashBook.CashVoucherType
            {
                Direction = OMTS.Domain.Enums.CashDirection.Payment, Category = OMTS.Domain.Enums.CashCategory.Payroll, Name = "Chi trả lương NV",
                CountsInResult = true, IsSystem = true, Manual = false, IsActive = true,
            };
            db.CashVoucherTypes.Add(type);
            db.Entry(type).Property("Id").CurrentValue = OMTS.Domain.Constants.CashVoucherTypeIds.PaymentPayroll;
        });
    }

    public static Guid GuidOf(this JsonElement element, string property = "id") => element.GetProperty(property).GetGuid();

    public static JsonElement SlipOf(this JsonElement sheet, Guid employee) =>
        sheet.GetProperty("slips").EnumerateArray().Single(s => s.GetProperty("employeeId").GetGuid() == employee).Clone();

    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static async Task<JsonElement> JsonAsync(this HttpClient client, string url) => await client.GetFromJsonAsync<JsonElement>(url);

    public static async Task<JsonElement> SheetAsync(this HttpClient client, Guid id) => await client.JsonAsync($"{Payrolls}/{id}");

    /// <summary>The cash book lines of the fund whose code starts with <paramref name="prefix"/>.</summary>
    public static async Task<JsonElement[]> CashLinesAsync(this HttpClient client, string fund, string prefix)
    {
        var page = await client.JsonAsync($"/api/cash-book?fund={fund}&search={prefix}&pageSize=100");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToArray();
    }

    public static async Task<decimal> PaidOnAsync(this AuthApiFactory factory, Guid slip) =>
        (await factory.QueryAsync(db => db.WagePayments.Where(w => w.PayrollLineId == slip && w.CashVoucher.CancelledAt == null).Select(w => w.Amount).ToListAsync())).Sum();
}
