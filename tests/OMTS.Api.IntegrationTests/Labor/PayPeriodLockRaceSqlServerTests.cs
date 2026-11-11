using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// BR-LABOR-13 on a real SQL Server: a dated change and the sending of a payroll cannot both pass unseen, whichever saves first.
/// The send here is what LABOR-12 will do: it updates the payroll row, under the row version it read.
/// </summary>
public class PayPeriodLockRaceSqlServerTests
{
    private static Payroll DraftOfOctober() => new() { PeriodStart = new(2020, 10, 1), PeriodEnd = new(2020, 10, 31) };

    [SqlServerFact]
    public async Task A_send_landing_between_the_lock_check_and_the_save_fails_that_save()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        await OwnerWithCooperativeAsync(api);
        var payroll = DraftOfOctober();
        await ChangeAsync(api, db => db.Payrolls.Add(payroll));

        using var scope = api.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<PayPeriodLock>().CheckAsync(new DateOnly(2020, 10, 15), default));

        await SendAsync(api, payroll.Id);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
    }

    [SqlServerFact]
    public async Task A_change_saved_before_a_send_that_read_the_payroll_earlier_fails_that_send()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobType = await AddJobTypeAsync(owner);
        var payroll = DraftOfOctober();
        await ChangeAsync(api, db => db.Payrolls.Add(payroll));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var sending = await db.Payrolls.SingleAsync(p => p.Id == payroll.Id);
        sending.StatusId = StatusIds.Payroll.AwaitingFinalization;

        Assert.Equal(HttpStatusCode.OK, (await owner.PostRateAsync(jobType, 50_000, "2020-10-15")).StatusCode);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
    }

    [SqlServerFact]
    public async Task Rates_and_settings_racing_the_send_of_their_period_never_answer_500_and_the_period_ends_locked()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobTypes = new List<JsonElement>();
        for (var i = 0; i < 8; i++)
        {
            jobTypes.Add(await AddJobTypeAsync(owner));
        }

        var version = (await owner.GetFromJsonAsync<JsonElement>(AttendanceSettings)).VersionOf();
        var payroll = DraftOfOctober();
        await ChangeAsync(api, db => db.Payrolls.Add(payroll));

        var saves = jobTypes.Select((j, i) => owner.PostRateAsync(j, 50_000 + i, "2020-10-15"))
            .Append(owner.PostAsJsonAsync(AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = "2020-10-15", rowVersion = version }))
            .ToList();
        var send = Task.Run(() => SendWithRetriesAsync(api, payroll.Id));
        var responses = await Task.WhenAll(saves);
        await send;

        await AssertNo500Async(responses);
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Contains(await refused.ErrorCodeAsync(), new[] { "MSG06", "MSG-LABOR-04" });
        }

        Assert.Equal(StatusIds.Payroll.AwaitingFinalization, await QueryAsync(api, db => db.Payrolls.Select(p => p.StatusId).SingleAsync()));
        var current = await owner.ReadJobTypeAsync(jobTypes[0].IdOf());
        Assert.Equal("MSG-LABOR-04", await (await owner.PostRateAsync(current, 70_000, "2020-10-16")).ErrorCodeAsync());
    }

    [SqlServerFact]
    public async Task Parallel_changes_of_employee_type_with_a_draft_payroll_in_the_way_never_answer_500()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var payroll = DraftOfOctober();
        await ChangeAsync(api, db => db.Payrolls.Add(payroll));
        var employees = new List<(string Name, string Phone, JsonElement Saved)>();
        for (var i = 0; i < 4; i++)
        {
            var (name, phone) = ($"Loại {i} {NewCode()}", NewPhone());
            employees.Add((name, phone, await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone.Id))));
        }

        var responses = await Task.WhenAll(employees.Select(e => owner.PutAsJsonAsync(
            $"/api/employees/{EmployeeRequests.IdOf(e.Saved)}",
            EmployeeRequests.Profile(e.Name, e.Phone, zone.Id, b =>
            {
                b["employeeType"] = "Staff";
                b["employeeTypeFrom"] = "2020-10-15";
                b["rowVersion"] = EmployeeRequests.RowVersionOf(e.Saved);
            }))));

        await AssertNo500Async(responses);
    }

    private static async Task SendAsync(SqlServerApiFactory api, Guid payrollId)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var payroll = await db.Payrolls.SingleAsync(p => p.Id == payrollId);
        payroll.StatusId = StatusIds.Payroll.AwaitingFinalization;
        await db.SaveChangesAsync();
    }

    /// <summary>A send that meets a save of the period reads the row again, as a user does after MSG06.</summary>
    private static async Task SendWithRetriesAsync(SqlServerApiFactory api, Guid payrollId)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SendAsync(api, payrollId);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 50)
            {
            }
        }
    }
}
