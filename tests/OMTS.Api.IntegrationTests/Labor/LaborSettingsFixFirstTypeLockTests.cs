using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// QA M2 of OMTS-227: a day before every type row has the first row's type, so a new earliest row, or another type on the first
/// date, moves earlier days. A locked payroll refuses that with MSG-LABOR-04 (BR-LABOR-13); each test has its own host.
/// </summary>
public class LaborSettingsFixFirstTypeLockTests
{
    private const string Staff = nameof(EmployeeType.Staff);
    private static readonly DateOnly LockedDay = new(2020, 10, 15);

    private sealed record NewEmployee(AuthApiFactory Factory, HttpClient Owner, Guid Id, string Name, string Phone, Guid Zone, JsonElement Saved)
    {
        public Task<HttpResponseMessage> PutTypeAsync(string type, string date) =>
            Owner.PutAsJsonAsync($"/api/employees/{Id}", EmployeeRequests.Profile(Name, Phone, Zone, b =>
            {
                b["employeeType"] = type;
                b["employeeTypeFrom"] = date;
                b["rowVersion"] = EmployeeRequests.RowVersionOf(Saved);
            }));

        public async Task<EmployeeType?> TypeOnAsync(DateOnly day) =>
            EmployeeTypePeriod.TypeOn(await Rows(), day);

        public Task<int> RowCountAsync() => Factory.QueryAsync(db => db.EmployeeTypePeriods.CountAsync(p => p.EmployeeId == Id));

        private Task<List<EmployeeTypePeriod>> Rows() => Factory.QueryAsync(db => db.EmployeeTypePeriods.AsNoTracking().Where(p => p.EmployeeId == Id).ToListAsync());
    }

    /// <summary>An employee with one type, Seasonal worker, from <paramref name="firstDate"/>.</summary>
    private static async Task<NewEmployee> SeasonalAsync(AuthApiFactory factory, string firstDate)
    {
        var owner = await OwnerAsync(factory);
        var zone = await factory.AddZoneAsync(NewCode());
        var (name, phone) = ($"Loại {NewCode()}", NewPhone());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone.Id, b => b["employeeTypeFrom"] = firstDate));
        return new NewEmployee(factory, owner, EmployeeRequests.IdOf(saved), name, phone, zone.Id, saved);
    }

    [Fact]
    public async Task A_new_earliest_type_after_the_lock_is_refused_and_says_which_date_to_choose()
    {
        await using var factory = new AuthApiFactory();
        var employee = await SeasonalAsync(factory, "2021-01-01");
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);

        var refused = await employee.PutTypeAsync(Staff, "2020-11-15");

        var problem = await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Equal("The payroll of 10/2020 is Finalized, so this change cannot apply to 31/10/2020. Choose a date after 01/01/2021.", problem.Detail());
        Assert.Equal("2021-01-01", problem.Str("lockedUntil"));
        Assert.Equal(1, await employee.RowCountAsync());
        Assert.Equal(EmployeeType.SeasonalWorker, await employee.TypeOnAsync(LockedDay));

        // The date it names moves no earlier day, so it saves.
        Assert.Equal(HttpStatusCode.OK, (await employee.PutTypeAsync(Staff, "2021-01-02")).StatusCode);
        Assert.Equal(EmployeeType.SeasonalWorker, await employee.TypeOnAsync(LockedDay));
    }

    [Fact]
    public async Task Another_type_on_the_first_date_is_refused_while_a_later_date_saves()
    {
        await using var factory = new AuthApiFactory();
        var employee = await SeasonalAsync(factory, "2020-11-01");
        await factory.AddAwaitingPayrollAsync("2020-10-01", "2020-10-31");

        var refused = await employee.PutTypeAsync(Staff, "2020-11-01");

        var problem = await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Equal(
            "The payroll of 10/2020 is Awaiting finalization, so this change cannot apply to 31/10/2020. Choose a date after 01/11/2020, or ask the Cooperative Owner to send the payroll back first.",
            problem.Detail());
        Assert.Equal(EmployeeType.SeasonalWorker, await employee.TypeOnAsync(new DateOnly(2020, 11, 1)));
        Assert.Equal(HttpStatusCode.OK, (await employee.PutTypeAsync(Staff, "2020-11-02")).StatusCode);
    }

    // The date entered is itself locked: it is the one named, and the date to choose is still after the first type's.
    [Fact]
    public async Task A_new_earliest_type_inside_the_lock_names_its_date()
    {
        await using var factory = new AuthApiFactory();
        var employee = await SeasonalAsync(factory, "2021-01-01");
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);

        var problem = await (await employee.PutTypeAsync(Staff, "2020-10-15")).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);

        Assert.Equal("The payroll of 10/2020 is Finalized, so this change cannot apply to 15/10/2020. Choose a date after 01/01/2021.", problem.Detail());
    }

    // Conservative: the first type is moved, whichever locked payroll holds the moved days, also one long before the new date.
    [Fact]
    public async Task A_new_earliest_type_is_refused_by_a_locked_payroll_far_before_it()
    {
        await using var factory = new AuthApiFactory();
        var employee = await SeasonalAsync(factory, "2022-06-01");
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);

        Assert.Equal("MSG-LABOR-04", await (await employee.PutTypeAsync(Staff, "2022-05-01")).ErrorCodeAsync());
    }

    [Theory]
    [InlineData(StatusIds.Payroll.Draft)]
    [InlineData(StatusIds.Payroll.SentBack)]
    public async Task A_payroll_that_is_not_locked_lets_it_through_and_is_touched_as_its_days_move(int statusId)
    {
        await using var factory = new AuthApiFactory();
        var employee = await SeasonalAsync(factory, "2021-01-01");
        var open = await factory.AddPayrollAsync("2020-10-01", "2020-10-31", statusId);

        Assert.Equal(HttpStatusCode.OK, (await employee.PutTypeAsync(Staff, "2020-11-15")).StatusCode);

        Assert.Equal(EmployeeType.Staff, await employee.TypeOnAsync(LockedDay));
        Assert.NotNull(await factory.QueryAsync(db => db.Payrolls.Where(p => p.Id == open.Id).Select(p => p.UpdatedAt).SingleAsync()));
    }

    [Fact]
    public async Task With_no_payroll_a_new_earliest_type_and_a_replaced_first_type_save()
    {
        await using var factory = new AuthApiFactory();
        var first = await SeasonalAsync(factory, "2021-01-01");
        var second = await SeasonalAsync(factory, "2021-01-01");

        Assert.Equal(HttpStatusCode.OK, (await first.PutTypeAsync(Staff, "2020-11-15")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.PutTypeAsync(Staff, "2021-01-01")).StatusCode);
    }

    // The type already in force moves nothing, so a date before the first row passes as before, adding no row.
    [Fact]
    public async Task The_type_already_in_force_on_an_earlier_date_is_no_change()
    {
        await using var factory = new AuthApiFactory();
        var employee = await SeasonalAsync(factory, "2021-01-01");
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);

        Assert.Equal(HttpStatusCode.OK, (await employee.PutTypeAsync(nameof(EmployeeType.SeasonalWorker), "2020-11-15")).StatusCode);

        Assert.Equal(1, await employee.RowCountAsync());
    }

    // BR-LABOR-13 as it was: a later type keeps the first row, so only its own date meets the lock.
    [Fact]
    public async Task A_later_type_after_the_lock_saves_and_moves_no_earlier_day()
    {
        await using var factory = new AuthApiFactory();
        var employee = await SeasonalAsync(factory, "2019-01-01");
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);

        Assert.Equal(HttpStatusCode.OK, (await employee.PutTypeAsync(Staff, "2020-11-01")).StatusCode);

        Assert.Equal(EmployeeType.SeasonalWorker, await employee.TypeOnAsync(LockedDay));
        Assert.Equal(EmployeeType.Staff, await employee.TypeOnAsync(new DateOnly(2020, 11, 1)));
    }
}
