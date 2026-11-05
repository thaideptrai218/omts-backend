using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-01.1.E1: what L1 left for this PR. A new employee type takes effect only after the last locked day; each test has its own host.</summary>
public class PayPeriodLockEmployeeTypeTests
{
    private const string October = "2020-10-01";
    private const string OctoberEnd = "2020-10-31";
    private const string NovemberFirst = "2020-11-01";

    [Fact]
    public async Task A_new_employee_type_dated_in_a_locked_period_is_refused_and_the_next_day_is_saved()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var zone = await factory.AddZoneAsync(NewCode());
        var (name, phone) = ($"Loại {NewCode()}", NewPhone());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone.Id));
        var id = EmployeeRequests.IdOf(saved);
        await factory.AddAwaitingPayrollAsync(October, OctoberEnd);
        Dictionary<string, object?> Edit(string type, string from) => EmployeeRequests.Profile(name, phone, zone.Id, b =>
        {
            b["employeeType"] = type;
            b["employeeTypeFrom"] = from;
            b["rowVersion"] = EmployeeRequests.RowVersionOf(saved);
        });

        var refused = await owner.PutAsJsonAsync($"/api/employees/{id}", Edit(nameof(EmployeeType.Staff), OctoberEnd));
        var problem = await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Contains("cannot apply to 31/10/2020", problem.Detail());
        Assert.Equal(1, await factory.QueryAsync(db => db.EmployeeTypePeriods.CountAsync(p => p.EmployeeId == id)));

        // The type already in force on that date is no change, so it passes.
        var sameType = await owner.PutAsJsonAsync($"/api/employees/{id}", Edit(nameof(EmployeeType.SeasonalWorker), OctoberEnd));
        Assert.Equal(HttpStatusCode.OK, sameType.StatusCode);

        var accepted = await owner.PutAsJsonAsync($"/api/employees/{id}", Edit(nameof(EmployeeType.Staff), NovemberFirst));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, await factory.QueryAsync(db => db.EmployeeTypePeriods.CountAsync(p => p.EmployeeId == id)));
    }

    [Fact]
    public async Task A_type_period_inside_a_locked_period_cannot_be_replaced()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var zone = await factory.AddZoneAsync(NewCode());
        var (name, phone) = ($"Thay {NewCode()}", NewPhone());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone.Id));
        var id = EmployeeRequests.IdOf(saved);
        var first = await owner.PutAsJsonAsync($"/api/employees/{id}", EmployeeRequests.Profile(name, phone, zone.Id, b =>
        {
            b["employeeType"] = nameof(EmployeeType.Staff);
            b["employeeTypeFrom"] = "2020-10-15";
            b["rowVersion"] = EmployeeRequests.RowVersionOf(saved);
        }));
        var edited = await first.BodyAsync(HttpStatusCode.OK);
        await factory.AddPayrollAsync(October, OctoberEnd, StatusIds.Payroll.Finalized);

        var refused = await owner.PutAsJsonAsync($"/api/employees/{id}", EmployeeRequests.Profile(name, phone, zone.Id, b =>
        {
            b["employeeType"] = nameof(EmployeeType.SeasonalWorker);
            b["employeeTypeFrom"] = "2020-10-15";
            b["rowVersion"] = EmployeeRequests.RowVersionOf(edited);
        }));

        Assert.Equal("MSG-LABOR-04", await refused.ErrorCodeAsync());
        Assert.Equal(EmployeeType.Staff, await factory.QueryAsync(db => db.EmployeeTypePeriods.Where(p => p.EmployeeId == id && p.EffectiveFrom == new DateOnly(2020, 10, 15)).Select(p => p.Type).SingleAsync()));
    }

    [Fact]
    public async Task A_new_employee_dated_in_a_locked_period_is_added_as_no_day_of_it_changes()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var zone = await factory.AddZoneAsync(NewCode());
        await factory.AddPayrollAsync(October, OctoberEnd, StatusIds.Payroll.Finalized);

        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile($"Mới {NewCode()}", NewPhone(), zone.Id, b => b["employeeTypeFrom"] = OctoberEnd));

        Assert.Equal(OctoberEnd, saved.GetProperty("employee").GetProperty("typeHistory")[0].Str("effectiveFrom"));
    }
}
