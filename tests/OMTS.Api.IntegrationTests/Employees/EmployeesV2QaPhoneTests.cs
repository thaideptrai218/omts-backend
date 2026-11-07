using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>QA of OMTS-227, the phone number: BR-LABOR-01 (optional, unique among Employed), MSG-LABOR-01 and MSG-LABOR-03.</summary>
public class EmployeesV2QaPhoneTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    public static TheoryData<string> Forms => new() { "plus84", "84", "spaces", "dots", "dashes", "brackets" };

    private static string Form(string phone, string form) => form switch
    {
        "plus84" => $"+84 {phone[1..]}",
        "84" => $"84{phone[1..]}",
        "spaces" => $"{phone[..4]} {phone[4..7]} {phone[7..]}",
        "dots" => $"{phone[..4]}.{phone[4..7]}.{phone[7..]}",
        "dashes" => $"{phone[..4]}-{phone[4..7]}-{phone[7..]}",
        "brackets" => $"({phone[..4]}) {phone[4..]}",
        _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Every_written_form_of_a_number_meets_the_same_rule_on_add_edit_and_resume(string form)
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var holder = IdOf(await CreateAsync(owner, Profile($"Holder {NewCode()}", phone, zone.Id)));
        var (editName, editPhone) = ($"Edit {NewCode()}", NewPhone());
        var toEdit = await CreateAsync(owner, Profile(editName, editPhone, zone.Id));
        var left = await EndAsync(owner, IdOf(await CreateAsync(owner, Profile($"Left {NewCode()}", NewPhone(), zone.Id))));
        var written = Form(phone, form);

        foreach (var refused in new[]
        {
            await owner.PostAsJsonAsync("/api/employees", Profile($"New {NewCode()}", written, zone.Id)),
            await owner.PutAsJsonAsync($"/api/employees/{IdOf(toEdit)}", Profile(editName, written, zone.Id).With("rowVersion", RowVersionOf(toEdit))),
            await owner.PostAsJsonAsync($"/api/employees/{IdOf(left)}/resume", new { rowVersion = RowVersionOf(left), phoneNumber = written }),
        })
        {
            var problem = await refused.JsonAsync(HttpStatusCode.Conflict);
            Assert.Equal("MSG-LABOR-03", problem.GetProperty("code").GetString());
            Assert.Equal(holder, problem.GetProperty("existingEmployeeId").GetGuid());
        }

        // The holder's own number in another form is no conflict, and is stored in its one form.
        var (ownName, ownPhone) = ($"Own {NewCode()}", NewPhone());
        var own = await CreateAsync(owner, Profile(ownName, ownPhone, zone.Id));
        var same = await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(own)}", Profile(ownName, Form(ownPhone, form), zone.Id).With("rowVersion", RowVersionOf(own)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(ownPhone, same.GetProperty("employee").GetProperty("phoneNumber").GetString());
    }

    // L3. LABOR-01.0 step 2: "searches by name or phone number"; the repository's comment promises +84… and 84….
    [Theory]
    [InlineData("plus84")]
    [InlineData("84")]
    [InlineData("spaces")]
    [InlineData("dots")]
    public async Task L3_the_list_finds_a_number_typed_in_any_form(string form)
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, Profile($"Find {NewCode()}", phone, zone.Id)));

        var found = await (await owner.GetAsync($"/api/employees?search={Uri.EscapeDataString(Form(phone, form))}")).JsonAsync(HttpStatusCode.OK);

        Assert.Contains(id, found.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task The_list_finds_a_number_typed_with_plus_84_and_no_separator_and_a_part_of_the_number()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, Profile($"Find {NewCode()}", phone, zone.Id)));

        foreach (var text in new[] { $"+84{phone[1..]}", phone, phone[3..] })
        {
            var found = await (await owner.GetAsync($"/api/employees?search={Uri.EscapeDataString(text)}")).JsonAsync(HttpStatusCode.OK);
            Assert.Contains(id, found.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        }
    }

    [Fact]
    public async Task Empty_and_blank_numbers_are_none_and_short_or_long_ones_are_refused()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        foreach (var none in new[] { null, string.Empty, "   " })
        {
            var saved = await CreateAsync(owner, Profile($"None {NewCode()}", none, zone.Id));
            Assert.Equal(JsonValueKind.Null, saved.GetProperty("employee").GetProperty("phoneNumber").ValueKind);
            Assert.False(saved.GetProperty("employee").GetProperty("hasStaffAccount").GetBoolean());
        }

        foreach (var bad in new[] { "123", "090000000", "09000000000", "abcdefghij", "1900000000", new string('9', 5000) })
        {
            var refused = await owner.PostAsJsonAsync("/api/employees", Profile($"Bad {NewCode()}", bad, zone.Id));
            Assert.Equal(("MSG-GEN-01", "Phone number"), (await refused.ErrorCodeAsync(), await FieldAsync(refused)));
        }
    }

    // BR-LABOR-01: a Left employee frees the number, also when both then want it back; the last working day today does not free it yet.
    [Fact]
    public async Task A_left_number_is_free_for_add_and_edit_and_blocks_the_resume_of_the_one_who_left()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var left = IdOf(await CreateAsync(owner, Profile($"Left {NewCode()}", phone, zone.Id)));
        await EndAsync(owner, left);
        var (editName, editPhone) = ($"Edit {NewCode()}", NewPhone());
        var editor = await CreateAsync(owner, Profile(editName, editPhone, zone.Id));

        var taken = await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(editor)}", Profile(editName, phone, zone.Id).With("rowVersion", RowVersionOf(editor)))).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(phone, taken.GetProperty("employee").GetProperty("phoneNumber").GetString());
        var version = await CurrentVersionAsync(owner, left);
        Assert.Equal("MSG-LABOR-03", await (await owner.PostAsJsonAsync($"/api/employees/{left}/resume", new { rowVersion = version })).ErrorCodeAsync());

        // The holder lets the number go and the first one takes it back.
        await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(editor)}", Profile(editName, null, zone.Id).With("rowVersion", RowVersionOf(taken)))).JsonAsync(HttpStatusCode.OK);
        var back = await (await owner.PostAsJsonAsync($"/api/employees/{left}/resume", new { rowVersion = version })).JsonAsync(HttpStatusCode.OK);
        Assert.Equal(phone, back.GetProperty("employee").GetProperty("phoneNumber").GetString());
        Assert.Equal("Employed", back.GetProperty("employee").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, back.GetProperty("employee").GetProperty("lastWorkingDay").ValueKind);
    }

    // LABOR-01 steps 4-6, E3: fields first, then the number, then the question; the Owner is not asked; nothing saved on refusal.
    [Fact]
    public async Task The_approval_comes_after_the_fields_and_the_number_and_a_refusal_saves_nothing()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var manager = await ManagerAsync(factory, zone);
        var phone = NewPhone();
        await CreateAsync(owner, Profile($"Holder {NewCode()}", phone, zone.Id));
        var name = $"Approval {NewCode()}";

        var missing = await manager.PostAsJsonAsync("/api/employees", Profile(string.Empty, NewPhone(), zone.Id));
        var taken = await manager.PostAsJsonAsync("/api/employees", Profile(name, phone, zone.Id));
        var asked = await manager.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id));
        var said = await manager.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id).With("ownerApproved", false));
        var problem = await asked.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-01", null);

        Assert.Equal("MSG-GEN-01", await missing.ErrorCodeAsync());
        Assert.Equal("MSG-LABOR-03", await taken.ErrorCodeAsync());
        Assert.Equal($"Has the Cooperative Owner approved, in person, adding {name} as a new employee?", problem.Detail());
        Assert.Equal("MSG-LABOR-01", await said.ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == name)));
        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Employee.Created" && (e.After ?? string.Empty).Contains(name))));

        var approved = await CreateAsync(manager, ApprovedProfile(name, NewPhone(), zone.Id));
        var row = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == IdOf(approved) && e.Action == "Employee.Created"));
        Assert.Contains("approved in person", row.Reason);
        Assert.Equal("MSG-GEN-05", approved.GetProperty("messageCode").GetString());

        var byOwner = await CreateAsync(owner, Profile($"By owner {NewCode()}", NewPhone(), zone.Id));
        Assert.Null(await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == IdOf(byOwner) && e.Action == "Employee.Created").Select(e => e.Reason).SingleAsync()));
    }

    // BR-LABOR-01, BR-ACCOUNT-02: the number may be that of a system account; only a staff account counts for hasStaffAccount.
    [Fact]
    public async Task A_number_of_any_system_account_is_allowed_on_an_employee_and_only_staff_counts()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var trader = await factory.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Trader);
        var staff = await factory.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff, a => a.StatusId = StatusIds.Account.Locked);

        var withTrader = await CreateAsync(owner, Profile($"Trader {NewCode()}", trader.PhoneNumber, zone.Id));
        var withStaff = await CreateAsync(owner, Profile($"Staff {NewCode()}", staff.PhoneNumber, zone.Id));

        Assert.False(withTrader.GetProperty("employee").GetProperty("hasStaffAccount").GetBoolean());
        Assert.True(withStaff.GetProperty("employee").GetProperty("hasStaffAccount").GetBoolean());
    }
}
