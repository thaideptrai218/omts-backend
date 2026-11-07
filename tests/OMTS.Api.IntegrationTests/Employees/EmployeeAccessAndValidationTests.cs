using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>LABOR-01 – every field rule (MSG-GEN-01), and the phone number unique among Employed employees (MSG-LABOR-03).</summary>
public class EmployeeAccessAndValidationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Every_invalid_field_is_named_and_no_profile_is_created()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var discontinued = await factory.AddZoneAsync(NewCode());
        await factory.UpdateAsync<FarmingZone>(discontinued.Id, z => z.StatusId = StatusIds.Usage.Discontinued);
        var name = $"Invalid {NewCode()}";

        var all = await owner.PostAsJsonAsync("/api/employees", new Dictionary<string, object?>
        {
            ["fullName"] = new string('N', 101),
            ["phoneNumber"] = "+1 202 555 0100",
            ["birthday"] = Today().AddDays(2).ToString("yyyy-MM-dd"),
            ["address"] = new string('A', 501),
            ["area"] = new string('R', 101),
            ["ward"] = new string('W', 101),
            ["otherInformation"] = new string('O', 1001),
            ["notes"] = new string('T', 1001),
            ["workingZoneIds"] = new[] { zone.Id, zone.Id },
            ["jobTypeIds"] = new[] { zone.Id, zone.Id },
            ["bankAccount"] = new { bankName = "VCB", accountNumber = "12345", accountHolderName = "A" },
        });
        var problem = await all.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, all.StatusCode);
        Assert.Equal(
            ["Full name", "Phone number", "Employee type", "Birthday", "Address", "Area", "Ward", "Other information", "Notes", "Working zones", "Job types", "Bank account"],
            problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));

        var single = new (string Field, Dictionary<string, object?> Body)[]
        {
            ("Full name", Profile(name, NewPhone(), zone.Id, b => b.Remove("fullName"))),
            ("Employee type", Profile(name, NewPhone(), zone.Id, b => b.Remove("employeeType"))),
            ("Working zones", Profile(name, NewPhone())),
            ("Working zones", Profile(name, NewPhone(), Guid.NewGuid())),
            ("Working zones", Profile(name, NewPhone(), discontinued.Id)),
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
            Assert.Equal($"{field} is missing or invalid. Please check and enter it again.", (await ProblemAsync(response)).GetProperty("detail").GetString());
        }

        // A body that cannot be read answers like any other invalid field, never 500.
        foreach (var (key, value) in new (string, object)[] { ("employeeType", "Boss"), ("employeeTypeFrom", "soon"), ("ownerApproved", "maybe"), ("jobTypeIds", new[] { "x" }) })
        {
            var unreadable = await owner.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id, b => b[key] = value));
            Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
            Assert.Equal("MSG-GEN-01", await unreadable.ErrorCodeAsync());
        }

        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == name || e.FullName.StartsWith("NNNN"))));
    }

    [Fact]
    public async Task Bank_group_of_6_and_20_digits_is_accepted_and_the_holder_is_stored_upper_case()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var six = await CreateAsync(owner, Profile($"Bank {NewCode()}", NewPhone(), zone.Id, b => b["bankAccount"] = Bank("999901")));
        var twenty = await CreateAsync(owner, Profile($"Bank {NewCode()}", NewPhone(), zone.Id, b =>
            b["bankAccount"] = new { bankName = " Agribank ", accountNumber = new string('9', 20), accountHolderName = " trần thị bình " }));

        Assert.Equal("999901", six.GetProperty("employee").GetProperty("bankAccount").GetProperty("accountNumber").GetString());
        var bank = twenty.GetProperty("employee").GetProperty("bankAccount");
        Assert.Equal(("Agribank", "TRẦN THỊ BÌNH"), (bank.GetProperty("bankName").GetString(), bank.GetProperty("accountHolderName").GetString()));
    }

    // LABOR-01.0.E2: the entries are kept, the employee is named and can be opened; the number is compared normalised.
    [Fact]
    public async Task The_phone_of_another_employed_employee_is_msg_labor_03_on_add_and_edit_and_a_profile_keeps_its_own()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Phạm Văn {NewCode()}", NewPhone());
        var firstSaved = await CreateAsync(owner, Profile(name, phone, zone.Id));
        var first = IdOf(firstSaved);
        var other = await CreateAsync(owner, Profile($"Other {NewCode()}", NewPhone(), zone.Id));

        var viaPlus84 = await owner.PostAsJsonAsync("/api/employees", Profile($"Another {NewCode()}", $"+84 {phone[1..]}", zone.Id));
        var onUpdate = await owner.PutAsJsonAsync($"/api/employees/{IdOf(other)}", Profile("Other", phone, zone.Id, b => b["rowVersion"] = RowVersionOf(other)));
        var sameNameOtherPhone = await owner.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id));
        var unchangedSelf = await owner.PutAsJsonAsync($"/api/employees/{first}", Profile(name, phone, zone.Id, b => b["rowVersion"] = RowVersionOf(firstSaved)));

        foreach (var conflict in new[] { viaPlus84, onUpdate })
        {
            var problem = await conflict.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-03", "MSG03");
            Assert.Equal(
                $"This phone number belongs to {name}. Each employee needs their own phone number for the employee app: enter another number, or leave it empty.",
                problem.Detail());
            Assert.Equal(first, problem.GetProperty("existingEmployeeId").GetGuid());
        }

        Assert.Equal(HttpStatusCode.Created, sameNameOtherPhone.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unchangedSelf.StatusCode);
        Assert.Equal(other.GetProperty("employee").GetProperty("fullName").GetString(),
            await factory.QueryAsync(db => db.Employees.Where(e => e.Id == IdOf(other)).Select(e => e.FullName).SingleAsync()));
    }

    // BR-LABOR-01: unique among Employed only, and it may be the phone number of a system account.
    [Fact]
    public async Task A_left_employee_or_a_system_account_may_have_the_number_of_a_new_employee()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var left = IdOf(await CreateAsync(owner, Profile($"Left {NewCode()}", phone, zone.Id)));
        await EndAsync(owner, left);
        var account = await factory.AddAccountAsync(NewCode(), Password, OMTS.Domain.Enums.SubjectType.Staff);

        var sameAsLeft = await CreateAsync(owner, Profile($"New {NewCode()}", phone, zone.Id));
        var sameAsAccount = await CreateAsync(owner, Profile($"Has account {NewCode()}", account.PhoneNumber, zone.Id));

        Assert.Equal(phone, sameAsLeft.GetProperty("employee").GetProperty("phoneNumber").GetString());
        Assert.True(sameAsAccount.GetProperty("employee").GetProperty("hasStaffAccount").GetBoolean());
        Assert.False(sameAsLeft.GetProperty("employee").GetProperty("hasStaffAccount").GetBoolean());
    }

    [Fact]
    public async Task Unknown_ids_and_a_missing_row_version_are_refused()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var saved = await CreateAsync(owner, Profile($"Rules {NewCode()}", NewPhone(), zone.Id));
        var id = IdOf(saved);
        var unknown = Guid.NewGuid();

        var accountOnUpdate = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile($"Rules {NewCode()}", NewPhone(), zone.Id, b =>
        {
            b["account"] = AccountBlock($"nv{NewCode()}", zone, role);
            b["rowVersion"] = RowVersionOf(saved);
        }));
        var noRowVersion = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile($"Rules {NewCode()}", NewPhone(), zone.Id));
        var endNoVersion = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { lastWorkingDay = Today().ToString("yyyy-MM-dd") });
        var version = RowVersionOf(saved);

        Assert.Equal("Account", await FieldAsync(accountOnUpdate));
        Assert.Equal("RowVersion", await FieldAsync(noRowVersion));
        Assert.Equal("RowVersion", await FieldAsync(endNoVersion));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/employees/{unknown}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/employees/{unknown}", Profile("X", NewPhone(), null, b => b["rowVersion"] = version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{unknown}/end-employment", new { lastWorkingDay = "2026-10-01", rowVersion = version })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{unknown}/resume", new { rowVersion = version })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{unknown}/working-zones", new { zoneId = zone.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{unknown}/account", AccountBlock($"nv{NewCode()}", zone, role))).StatusCode);
    }
}
