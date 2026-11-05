using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// QA of OMTS-216 PR 0.2 on LABOR-01 (SRS 27/10 Part II §9.1): MSG-LABOR-03, MSG-ACCOUNT-09 and MSG-GEN-05 with their
/// texts and legacy codes.
/// </summary>
public class EmployeeMessageCodeQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // LABOR-01 step 7 (MSG-GEN-05) and LABOR-01.0.E2: MSG-LABOR-03 naming the other employee (MSG03 "Employee profile already exists" before).
    [Fact]
    public async Task Saved_employee_carries_msg_gen_05_and_the_same_phone_is_msg_labor_03_naming_them()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Phạm Thị {NewCode()}", NewPhone());

        var saved = await (await owner.PostAsJsonAsync("/api/employees", Profile(name, phone, zone.Id))).BodyAsync(HttpStatusCode.Created);
        var same = await (await owner.PostAsJsonAsync("/api/employees", Profile(name, phone, zone.Id)))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-03", "MSG03");

        Assert.Equal(("Employee profile saved.", "MSG-GEN-05"), (saved.Str("message"), saved.Str("messageCode")));
        Assert.Equal(
            $"This phone number belongs to {name}. Each employee needs their own phone number for the employee app: enter another number, or leave it empty.",
            same.Detail());
        Assert.Equal(IdOf(saved), same.GetProperty("existingEmployeeId").GetGuid());
    }

    // LABOR-01.5.E1 / E2: MSG-ACCOUNT-09 naming the field (MSG03 "Account already exists" before).
    [Fact]
    public async Task Account_created_with_an_employee_and_a_taken_username_or_phone_is_msg_account_09()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var taken = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var username = await (await owner.PostAsJsonAsync("/api/employees", Profile($"A {NewCode()}", NewPhone(), zone.Id, b => b["account"] = AccountBlock(taken.Username, zone, role))))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-ACCOUNT-09", "MSG03");
        var phone = await (await owner.PostAsJsonAsync("/api/employees", Profile($"B {NewCode()}", taken.PhoneNumber, zone.Id, b => b["account"] = AccountBlock($"nv{NewCode()}", zone, role))))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-ACCOUNT-09", "MSG03");

        Assert.Equal("Username", username.Str("field"));
        Assert.Equal(taken.Id, username.GetProperty("existingId").GetGuid());
        Assert.Equal("This phone number is already used by another account. Please enter another one.", phone.Detail());
        Assert.Equal("Phone number", phone.Str("field"));
    }

    // GBR-01 on the employee status (MSG61 before).
    [Fact]
    public async Task Ending_a_left_employee_again_is_msg_gen_09_with_legacy_msg61()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var id = IdOf(await CreateAsync(owner, Profile($"C {NewCode()}", NewPhone(), zone.Id)));

        await EndAsync(owner, id);
        var again = await (await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", await EndBodyAsync(owner, id, Today())))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        Assert.Equal("Employee is Left and cannot be used for this action.", again.Detail());
    }
}
