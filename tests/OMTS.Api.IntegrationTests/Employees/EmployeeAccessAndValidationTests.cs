using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>LAB-01 – Owner-only access on every endpoint, every field rule (MSG01) and the one-profile-per-person rule (MSG03).</summary>
public class EmployeeAccessAndValidationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Staff_with_labor_rights_traders_and_anonymous_callers_are_refused_on_every_endpoint()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var id = IdOf(await CreateAsync(owner, Profile($"Access {NewCode()}", NewPhone(), zone.Id)));
        var manager = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(manager, await factory.AddRoleAsync($"ZM {NewCode()}",
            (PermissionFunctions.LaborWorkers, true, true, true), (PermissionFunctions.WorkAssignments, true, true, true),
            (PermissionFunctions.Attendance, true, true, true), (PermissionFunctions.Payroll, true, true, true)), null);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var body = Profile($"Intruder {NewCode()}", NewPhone(), zone.Id);
        var account = AccountBlock($"nv{NewCode()}", zone, role);

        var calls = new Func<HttpClient, Task<HttpResponseMessage>>[]
        {
            c => c.GetAsync("/api/employees"),
            c => c.GetAsync("/api/employees/linkable-accounts"),
            c => c.GetAsync($"/api/employees/{id}"),
            c => c.PostAsJsonAsync("/api/employees", body),
            c => c.PutAsJsonAsync($"/api/employees/{id}", body),
            c => c.PostAsJsonAsync($"/api/employees/{id}/account", account),
            c => c.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = "2026-10-01" }),
            c => c.PostAsync($"/api/employees/{id}/reinstate", null),
        };

        foreach (var client in new[] { await factory.LoginAsync(manager.Username), await factory.LoginAsync(trader.Username) })
        {
            foreach (var call in calls)
            {
                Assert.Equal("MSG02", await (await call(client)).ErrorCodeAsync());
            }
        }

        foreach (var call in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await call(factory.CreateClient())).StatusCode);
        }

        var stored = await factory.QueryAsync(db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == id));
        Assert.Equal((StatusIds.Employee.Working, (Guid?)null), (stored.StatusId, stored.AccountId));
        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == (string)body["fullName"]!)));
    }

    [Fact]
    public async Task Every_invalid_field_is_named_and_no_profile_is_created()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var name = $"Invalid {NewCode()}";

        var all = await owner.PostAsJsonAsync("/api/employees", new Dictionary<string, object?>
        {
            ["fullName"] = new string('N', 101),
            ["phoneNumber"] = "+1 202 555 0100",
            ["birthday"] = DateOnly.FromDateTime(DateTime.Today.AddDays(2)).ToString("yyyy-MM-dd"),
            ["address"] = new string('A', 501),
            ["area"] = new string('R', 101),
            ["ward"] = new string('W', 101),
            ["otherInformation"] = new string('O', 1001),
            ["notes"] = new string('T', 1001),
            ["workingZoneIds"] = new[] { zone.Id, zone.Id },
            ["bankAccount"] = new { bankName = "VCB", accountNumber = "12345", accountHolderName = "A" },
        });
        var problem = await all.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, all.StatusCode);
        Assert.Equal(
            ["Full name", "Phone number", "Birthday", "Address", "Area", "Ward", "Other information", "Notes", "Working zones", "Bank account"],
            problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));

        var single = new (string Field, Dictionary<string, object?> Body)[]
        {
            ("Full name", Profile(name, NewPhone(), zone.Id, b => b.Remove("fullName"))),
            ("Phone number", Profile(name, NewPhone(), zone.Id, b => b.Remove("phoneNumber"))),
            ("Working zones", Profile(name, NewPhone(), Guid.NewGuid())),
            ("Bank account", Profile(name, NewPhone(), zone.Id, b => b["bankAccount"] = Bank("12345678ab"))),
            ("Bank account", Profile(name, NewPhone(), zone.Id, b => b["bankAccount"] = Bank(new string('1', 21)))),
            ("Bank account", Profile(name, NewPhone(), zone.Id, b => b["bankAccount"] = new { bankName = "VCB", accountNumber = "123456", accountHolderName = new string('H', 101) })),
            ("Bank account", Profile(name, NewPhone(), zone.Id, b => b["bankAccount"] = new { accountNumber = "123456", accountHolderName = "A" })),
        };

        foreach (var (field, body) in single)
        {
            var response = await owner.PostAsJsonAsync("/api/employees", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(field, await FieldAsync(response));
            Assert.Equal($"{field} is invalid. Please check and enter it again.", (await ProblemAsync(response)).GetProperty("detail").GetString());
        }

        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == name || e.FullName.StartsWith("NNNN"))));
    }

    [Fact]
    public async Task Bank_group_of_6_and_20_digits_is_accepted_and_the_holder_is_stored_upper_case()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var six = await CreateAsync(owner, Profile($"Bank {NewCode()}", NewPhone(), zone.Id, b => b["bankAccount"] = Bank("123456")));
        var twenty = await CreateAsync(owner, Profile($"Bank {NewCode()}", NewPhone(), zone.Id, b =>
            b["bankAccount"] = new { bankName = " Agribank ", accountNumber = new string('9', 20), accountHolderName = " trần thị bình " }));

        Assert.Equal("123456", six.GetProperty("employee").GetProperty("bankAccount").GetProperty("accountNumber").GetString());
        var bank = twenty.GetProperty("employee").GetProperty("bankAccount");
        Assert.Equal(("Agribank", "TRẦN THỊ BÌNH"), (bank.GetProperty("bankName").GetString(), bank.GetProperty("accountHolderName").GetString()));
    }

    [Fact]
    public async Task Same_person_is_found_through_plus84_and_on_update_and_nothing_changes()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Phạm Văn {NewCode()}", NewPhone());
        var firstSaved = await CreateAsync(owner, Profile(name, phone, zone.Id));
        var first = IdOf(firstSaved);
        var other = await CreateAsync(owner, Profile($"Other {NewCode()}", NewPhone(), zone.Id));

        var viaPlus84 = await owner.PostAsJsonAsync("/api/employees", Profile(name.ToLowerInvariant(), $"+84 {phone[1..]}", zone.Id));
        var onUpdate = await owner.PutAsJsonAsync($"/api/employees/{IdOf(other)}", Profile(name, phone, zone.Id, b => b["rowVersion"] = RowVersionOf(other)));
        var sameNameOtherPhone = await owner.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id));
        var unchangedSelf = await owner.PutAsJsonAsync($"/api/employees/{first}", Profile(name, phone, zone.Id, b => b["rowVersion"] = RowVersionOf(firstSaved)));

        foreach (var conflict in new[] { viaPlus84, onUpdate })
        {
            var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("Employee profile already exists. The system opens the existing record for you to check and does not create a copy.",
                problem.GetProperty("detail").GetString());
            Assert.Equal(first, problem.GetProperty("existingEmployeeId").GetGuid());
        }

        Assert.Equal(HttpStatusCode.Created, sameNameOtherPhone.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unchangedSelf.StatusCode);
        Assert.Equal(other.GetProperty("employee").GetProperty("fullName").GetString(),
            await factory.QueryAsync(db => db.Employees.Where(e => e.Id == IdOf(other)).Select(e => e.FullName).SingleAsync()));
    }

    [Fact]
    public async Task Account_rules_unknown_ids_and_a_missing_row_version_are_refused()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var saved = await CreateAsync(owner, Profile($"Rules {NewCode()}", NewPhone(), zone.Id));
        var id = IdOf(saved);
        var unknown = Guid.NewGuid();

        var both = await owner.PostAsJsonAsync("/api/employees", Profile($"Both {NewCode()}", NewPhone(), zone.Id, b =>
        {
            b["accountId"] = staff.Id;
            b["account"] = AccountBlock($"nv{NewCode()}", zone, role);
        }));
        var noSuchAccount = await owner.PostAsJsonAsync("/api/employees", Profile($"Ghost {NewCode()}", NewPhone(), zone.Id, b => b["accountId"] = Guid.NewGuid()));
        var accountOnUpdate = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile($"Rules {NewCode()}", NewPhone(), zone.Id, b =>
        {
            b["account"] = AccountBlock($"nv{NewCode()}", zone, role);
            b["rowVersion"] = RowVersionOf(saved);
        }));
        var noRowVersion = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile($"Rules {NewCode()}", NewPhone(), zone.Id));

        Assert.Equal("Account", await FieldAsync(both));
        Assert.Equal("Account", await FieldAsync(noSuchAccount));
        Assert.Equal("Account", await FieldAsync(accountOnUpdate));
        Assert.Equal("RowVersion", await FieldAsync(noRowVersion));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/employees/{unknown}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/employees/{unknown}", Profile("X", NewPhone(), null, b => b["rowVersion"] = RowVersionOf(saved)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{unknown}/end-employment", new { endDate = "2026-10-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/employees/{unknown}/reinstate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{unknown}/account", AccountBlock($"nv{NewCode()}", zone, role))).StatusCode);
        Assert.Null(await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.AccountId == staff.Id)));
    }
}
