using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileSqlServerTests;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// LAB-01 behaviour the in-memory provider cannot show: column lengths, the owned bank group and the
/// working-zone junction on SQL Server, collation-based search, real rowversion and the account link index.
/// </summary>
public class EmployeeSqlServerTests
{
    [SqlServerFact]
    public async Task Profile_at_every_maximum_length_round_trips_and_bank_and_zones_can_be_replaced()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var (first, second) = (await AddZoneAsync(api, cooperative.Id), await AddZoneAsync(api, cooperative.Id));
        var phone = NewPhone();

        var saved = await CreateAsync(owner, Profile(Vietnamese(100), phone, first.Id, b =>
        {
            b["address"] = Vietnamese(500);
            b["area"] = Vietnamese(100);
            b["ward"] = Vietnamese(100);
            b["otherInformation"] = Vietnamese(1000);
            b["notes"] = Vietnamese(1000);
            b["bankAccount"] = new { bankName = Vietnamese(100), accountNumber = new string('1', 20), accountHolderName = Vietnamese(100) };
        }));
        var id = IdOf(saved);
        var stored = await QueryAsync(api, db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == id));

        Assert.Equal((Vietnamese(100), Vietnamese(1000), new string('1', 20)), (stored.FullName, stored.Notes, stored.BankAccount?.AccountNumber));
        Assert.Equal(Vietnamese(100).ToUpperInvariant(), stored.BankAccount?.AccountHolderName);

        var updated = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(Vietnamese(100), phone, second.Id, b => b["rowVersion"] = RowVersionOf(saved)));
        var view = (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("employee");

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("bankAccount").ValueKind);
        Assert.Equal([second.Id], view.GetProperty("workingZones").EnumerateArray().Select(z => z.GetProperty("id").GetGuid()));
        Assert.Null(await QueryAsync(api, db => db.Employees.AsNoTracking().Where(e => e.Id == id).Select(e => e.BankAccount).SingleAsync()));
        Assert.Equal([second.Id], await QueryAsync(api, db => db.EmployeeWorkingZones.Where(w => w.EmployeeId == id).Select(w => w.ZoneId).ToListAsync()));
    }

    [SqlServerFact]
    public async Task Search_and_the_same_person_rule_ignore_letter_case()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var (name, phone) = ($"Nguyễn Thị Hoa {NewCode()}", NewPhone());
        var id = IdOf(await CreateAsync(owner, Profile(name, phone, zone.Id)));

        var lower = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={Uri.EscapeDataString(name.ToLowerInvariant())}");
        var upper = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={Uri.EscapeDataString(name.ToUpperInvariant())}");
        var duplicate = await owner.PostAsJsonAsync("/api/employees", Profile($" {name.ToUpperInvariant()} ", phone, zone.Id));

        Assert.Equal([id], lower.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([id], upper.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal("MSG-LABOR-03", await duplicate.ErrorCodeAsync());
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync()));
    }

    [SqlServerFact]
    public async Task Stale_row_version_is_MSG06_and_nothing_changes()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var (name, phone) = ($"Trần Văn {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));
        var id = IdOf(saved);

        var firstEdit = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b => { b["notes"] = "Lần 1"; b["rowVersion"] = RowVersionOf(saved); }));
        var staleEdit = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b => { b["notes"] = "Lần 2"; b["rowVersion"] = RowVersionOf(saved); }));
        var problem = await staleEdit.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, firstEdit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleEdit.StatusCode);
        Assert.Equal("MSG06", problem.GetProperty("code").GetString());
        Assert.StartsWith($"The data was just updated by {account.DisplayName} at ", problem.GetProperty("detail").GetString());
        Assert.Equal("Lần 1", await QueryAsync(api, db => db.Employees.Where(e => e.Id == id).Select(e => e.Notes).SingleAsync()));
    }

    // Two requests linking the same account at once: the database index keeps one link only.
    [SqlServerFact]
    public async Task Parallel_links_of_one_account_leave_a_single_link()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var staff = await api.AddAccountAsync($"nv{NewCode()}", Password, SubjectType.Staff);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            owner.PostAsJsonAsync("/api/employees", Profile($"Link {i} {NewCode()}", NewPhone(), zone.Id, b => b["accountId"] = staff.Id))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync(e => e.AccountId == staff.Id)));
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync()));
    }

    // Employee, account, grants and link are one save: a refused account leaves no row behind.
    [SqlServerFact]
    public async Task Employee_with_account_is_saved_together_or_not_at_all()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var role = new Role { Name = $"Tổ trưởng {NewCode()}" };
        role.Permissions.Add(new RolePermission { FunctionCode = Application.Features.Authorization.PermissionFunctions.Attendance, CanView = true });
        await ChangeAsync(api, db => db.Roles.Add(role));
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
