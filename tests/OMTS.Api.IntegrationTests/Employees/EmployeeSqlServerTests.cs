using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileSqlServerTests;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// LABOR-01 behaviour the in-memory provider cannot show: column lengths, the owned bank group, the rows of a profile on SQL
/// Server, collation-based search, and the index of an Employed phone number.
/// </summary>
public class EmployeeSqlServerTests
{
    [SqlServerFact]
    public async Task Profile_at_every_maximum_length_round_trips_and_bank_zones_and_job_types_can_be_replaced()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var (first, second) = (await AddZoneAsync(api, cooperative.Id), await AddZoneAsync(api, cooperative.Id));
        var (harvest, seed) = (new JobType { Name = Vietnamese(100) }, new JobType { Name = $"Treo giống {NewCode()}" });
        await ChangeAsync(api, db => db.JobTypes.AddRange(harvest, seed));
        var phone = NewPhone();

        var saved = await CreateAsync(owner, Profile(Vietnamese(100), phone, first.Id, b =>
        {
            b["address"] = Vietnamese(500);
            b["area"] = Vietnamese(100);
            b["ward"] = Vietnamese(100);
            b["otherInformation"] = Vietnamese(1000);
            b["notes"] = Vietnamese(1000);
            b["jobTypeIds"] = new[] { harvest.Id };
            b["bankAccount"] = new { bankName = Vietnamese(100), accountNumber = new string('9', 20), accountHolderName = Vietnamese(100) };
        }));
        var id = IdOf(saved);
        var stored = await QueryAsync(api, db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == id));

        Assert.Equal((Vietnamese(100), Vietnamese(1000), new string('9', 20)), (stored.FullName, stored.Notes, stored.BankAccount?.AccountNumber));
        Assert.Equal(Vietnamese(100).ToUpperInvariant(), stored.BankAccount?.AccountHolderName);

        var updated = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(Vietnamese(100), phone, second.Id, b => { b["jobTypeIds"] = new[] { seed.Id }; b["rowVersion"] = RowVersionOf(saved); }));
        var view = (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("employee");

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("bankAccount").ValueKind);
        Assert.Equal([second.Id], view.GetProperty("workingZones").EnumerateArray().Select(z => z.GetProperty("id").GetGuid()));
        Assert.Null(await QueryAsync(api, db => db.Employees.AsNoTracking().Where(e => e.Id == id).Select(e => e.BankAccount).SingleAsync()));
        Assert.Equal([second.Id], await QueryAsync(api, db => db.EmployeeWorkingZones.Where(w => w.EmployeeId == id).Select(w => w.ZoneId).ToListAsync()));
        Assert.Equal([seed.Id], await QueryAsync(api, db => db.EmployeeJobTypes.Where(j => j.EmployeeId == id).Select(j => j.JobTypeId).ToListAsync()));
    }

    [SqlServerFact]
    public async Task Search_ignores_letter_case_and_employees_without_a_phone_do_not_share_the_index()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var (name, phone) = ($"Nguyễn Thị Hoa {NewCode()}", NewPhone());
        var id = IdOf(await CreateAsync(owner, Profile(name, phone, zone.Id)));

        var lower = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={Uri.EscapeDataString(name.ToLowerInvariant())}");
        var upper = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={Uri.EscapeDataString(name.ToUpperInvariant())}");
        await CreateAsync(owner, Profile($"Không số {NewCode()}", null, zone.Id));
        await CreateAsync(owner, Profile($"Không số {NewCode()}", null, zone.Id));

        Assert.Equal([id], lower.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([id], upper.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(2, await QueryAsync(api, db => db.Employees.CountAsync(e => e.PhoneNumber == null)));
    }

    // Left employees keep their number, and it may be taken again; resuming them then needs another one.
    [SqlServerFact]
    public async Task The_number_of_a_left_employee_can_be_used_and_resuming_them_is_refused_until_it_is_free()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var phone = NewPhone();
        var left = IdOf(await CreateAsync(owner, Profile($"Nghỉ {NewCode()}", phone, zone.Id)));
        var ended = await EndAsync(owner, left);
        await CreateAsync(owner, Profile($"Mới {NewCode()}", phone, zone.Id));

        var taken = await owner.PostAsJsonAsync($"/api/employees/{left}/resume", new { rowVersion = RowVersionOf(ended) });
        var cleared = await (await owner.PostAsJsonAsync($"/api/employees/{left}/resume", new { rowVersion = RowVersionOf(ended), phoneNumber = "" })).BodyAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-LABOR-03", await taken.ErrorCodeAsync());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("employee").GetProperty("phoneNumber").ValueKind);
        Assert.Equal(2, await QueryAsync(api, db => db.Employees.CountAsync(e => e.StatusId == StatusIds.Employee.Working)));
    }

    // Employee, account, grants and link are one save: a refused account leaves no row behind.
    [SqlServerFact]
    public async Task Employee_with_account_is_saved_together_or_not_at_all()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var role = await api.RoleAsync(StaffRole.ZoneManager);
        var taken = NewPhone();
        await ChangeAsync(api, db => db.Accounts.Add(new Account
        {
            Username = $"nv{NewCode()}", DisplayName = "Đã có", PasswordHash = "x", SubjectType = SubjectType.Staff,
            PhoneNumber = taken, StatusId = StatusIds.Account.Active,
        }));
        var (refusedUser, acceptedUser) = ($"nv{NewCode()}", $"nv{NewCode()}");

        var refused = await owner.PostAsJsonAsync("/api/employees", Profile($"Refused {NewCode()}", taken, zone.Id, b => b["account"] = AccountBlock(refusedUser, zone, role)));
        var accepted = await CreateAsync(owner, Profile($"Accepted {NewCode()}", NewPhone(), zone.Id, b => b["account"] = AccountBlock(acceptedUser, zone, role)));

        Assert.Equal("MSG-ACCOUNT-09", await refused.ErrorCodeAsync());
        Assert.False(await QueryAsync(api, db => db.Accounts.AnyAsync(a => a.Username == refusedUser)));
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync()));
        var accountId = await QueryAsync(api, db => db.Accounts.Where(a => a.Username == acceptedUser).Select(a => a.Id).SingleAsync());
        Assert.Equal(accountId, await QueryAsync(api, db => db.Employees.Where(e => e.Id == IdOf(accepted)).Select(e => e.AccountId).SingleAsync()));
        Assert.Equal(1, await QueryAsync(api, db => db.AccessGrants.CountAsync(g => g.AccountId == accountId)));
    }
}
