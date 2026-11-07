using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// Set-up of the LABOR-11, LABOR-15 and LABOR-16 tests. Dates are fixed, far from today, so nothing is computed when tests are
/// discovered and a run may cross midnight: rates and settings date from 2020, later than the payrolls of a test only as it says.
/// </summary>
internal static class LaborSettingsTestKit
{
    public const string JobTypes = "/api/job-types";
    public const string AttendanceSettings = "/api/attendance-settings";
    public const string PayrollSettings = "/api/payroll-settings";

    public static async Task<HttpClient> OwnerAsync(AuthApiFactory factory)
    {
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password);
        return await factory.LoginAsync(account.Username, password);
    }

    public static async Task<HttpClient> StaffAsync(AuthApiFactory factory, StaffRole role, bool allZones = true)
    {
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await factory.GrantAsync(account, role, allZones ? null : await factory.AddZoneAsync(NewCode()));
        return await factory.LoginAsync(account.Username, password);
    }

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd");

    /// <summary>Adds a job type; the answer holds <c>jobType</c>.</summary>
    public static async Task<JsonElement> AddJobTypeAsync(HttpClient client, string? name = null, decimal rate = 40_000, string from = "2020-01-01")
    {
        var response = await client.PostAsJsonAsync(JobTypes, new { name = name ?? $"Job {NewCode()}", hourlyRate = rate, effectiveFrom = from });
        return (await response.BodyAsync(HttpStatusCode.Created)).GetProperty("jobType").Clone();
    }

    public static Task<HttpResponseMessage[]> InParallelAsync(int count, Func<int, Task<HttpResponseMessage>> call) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(call));

    /// <summary>One winner; each loser is 409 with one of the codes. A failure keeps the whole answer.</summary>
    public static async Task AssertOneWinnerAsync(HttpResponseMessage[] responses, HttpStatusCode won, params string[] loserCodes)
    {
        await AssertNo500Async(responses);
        foreach (var loser in responses.Where(r => r.StatusCode != won))
        {
            Assert.Contains(await loser.ErrorCodeAsync(), loserCodes);
        }

        Assert.Single(responses, r => r.StatusCode == won);
    }

    /// <summary>Every answer is 2xx or 409; a failure keeps the whole answer.</summary>
    public static async Task AssertNo500Async(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            await response.Content.LoadIntoBufferAsync();
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {text}");
        }
    }

    public static Guid IdOf(this JsonElement jobType) => jobType.GetProperty("id").GetGuid();

    public static string VersionOf(this JsonElement view) => view.GetProperty("rowVersion").GetString()!;

    public static Task<HttpResponseMessage> PostRateAsync(this HttpClient client, JsonElement jobType, decimal rate, string from) =>
        client.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/rates", new { hourlyRate = rate, effectiveFrom = from, rowVersion = jobType.VersionOf() });

    public static async Task<JsonElement> ReadJobTypeAsync(this HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<JsonElement>($"{JobTypes}/{id}");

    /// <summary>The rates of a view, (rate, date), current first, then the others as they come.</summary>
    public static IReadOnlyList<(decimal Rate, string From)> RatesOf(JsonElement jobType)
    {
        var rates = new List<(decimal, string)>();
        if (jobType.GetProperty("currentRate") is { ValueKind: JsonValueKind.Object } current)
        {
            rates.Add(RateOf(current));
        }

        rates.AddRange(jobType.GetProperty("otherRates").EnumerateArray().Select(RateOf));
        return rates;
    }

    public static (decimal Rate, string From) RateOf(JsonElement rate) => (rate.GetProperty("hourlyRate").GetDecimal(), rate.GetProperty("effectiveFrom").GetString()!);

    public static async Task<Payroll> AddPayrollAsync(this AuthApiFactory factory, string start, string end, int statusId)
    {
        var payroll = new Payroll { PeriodStart = DateOnly.Parse(start), PeriodEnd = DateOnly.Parse(end), StatusId = statusId };
        await factory.UseDbAsync(db => db.Payrolls.Add(payroll));
        return payroll;
    }

    public static Task<Payroll> AddAwaitingPayrollAsync(this AuthApiFactory factory, string start, string end) =>
        factory.AddPayrollAsync(start, end, StatusIds.Payroll.AwaitingFinalization);

    public static Task<int> AuditCountAsync(this AuthApiFactory factory, string action) =>
        factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == action));

    public static Task<string?> LastAuditBeforeAsync(this AuthApiFactory factory, string action) =>
        factory.QueryAsync(db => db.AuditEvents.Where(e => e.Action == action).OrderByDescending(e => e.OccurredAt).Select(e => e.Before).FirstAsync());
}
