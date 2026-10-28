using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>LAB-01 – employee profiles, with the login account created in the same save (ACS-09).</summary>
public class EmployeeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Owner_creates_and_searches_employees_with_zones_and_masked_bank_audit()
    {
        var (owner, zone, _) = await SetupAsync();
        var name = $"Trần Thị {NewCode()}";

        var created = await owner.PostAsJsonAsync("/api/employees", Body(name, NewPhone(), zone.Id, bank: true));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("employee").GetProperty("id").GetGuid();
        var found = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={Uri.EscapeDataString(name)}&zoneId={zone.Id}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Employee profile saved.", body.GetProperty("message").GetString());
        Assert.Equal("NGUYEN VAN A", body.GetProperty("employee").GetProperty("bankAccount").GetProperty("accountHolderName").GetString());
        Assert.Equal(zone.Code, body.GetProperty("employee").GetProperty("workingZones")[0].GetProperty("code").GetString());
        Assert.Equal(1, found.GetProperty("total").GetInt32());
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == id && e.Action == "Employee.Created"));
        Assert.Contains("******7890", audit.After);
        Assert.DoesNotContain("1234567890", audit.After);
    }

    [Fact]
    public async Task Same_person_invalid_fields_and_unusable_zones_are_refused()
    {
        var (owner, zone, _) = await SetupAsync();
        var discontinued = await factory.AddZoneAsync(NewCode());
        await factory.UpdateAsync<FarmingZone>(discontinued.Id, z => z.StatusId = StatusIds.Usage.Discontinued);
        var (name, phone) = ($"Lê Văn {NewCode()}", NewPhone());
        var first = await (await owner.PostAsJsonAsync("/api/employees", Body(name, phone, zone.Id))).Content.ReadFromJsonAsync<JsonElement>();

        var same = await owner.PostAsJsonAsync("/api/employees", Body($"  {name.ToUpperInvariant()} ", phone, zone.Id));
        var badPhone = await owner.PostAsJsonAsync("/api/employees", Body(name, "123", zone.Id));
        var halfBank = await owner.PostAsJsonAsync("/api/employees", new { fullName = name, phoneNumber = NewPhone(), bankAccount = new { bankName = "VCB" } });
        var badZone = await owner.PostAsJsonAsync("/api/employees", Body(name, NewPhone(), discontinued.Id));

        Assert.Equal("MSG-LABOR-03", await same.ErrorCodeAsync());
        Assert.Equal(first.GetProperty("employee").GetProperty("id").GetGuid(), (await same.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingEmployeeId").GetGuid());
        Assert.Equal("MSG-GEN-01", await badPhone.ErrorCodeAsync());
        Assert.Equal("Bank account is missing or invalid. Please check and enter it again.", (await halfBank.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal("MSG-GEN-01", await badZone.ErrorCodeAsync());
    }

    [Fact]
    public async Task Linking_needs_an_internal_account_not_linked_elsewhere()
    {
        var (owner, zone, _) = await SetupAsync();
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var first = await owner.PostAsJsonAsync("/api/employees", Body($"A {NewCode()}", NewPhone(), zone.Id, accountId: staff.Id));
        var linkable = await owner.GetFromJsonAsync<JsonElement>("/api/employees/linkable-accounts");

        var toTrader = await owner.PostAsJsonAsync("/api/employees", Body($"B {NewCode()}", NewPhone(), zone.Id, accountId: trader.Id));
        var twice = await owner.PostAsJsonAsync("/api/employees", Body($"C {NewCode()}", NewPhone(), zone.Id, accountId: staff.Id));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.DoesNotContain(staff.Id, linkable.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()));
        Assert.Equal("Account is missing or invalid. Please check and enter it again.", (await toTrader.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal("MSG24", await twice.ErrorCodeAsync());
    }

    [Fact]
    public async Task Account_created_with_the_employee_returns_the_temporary_password_once()
    {
        var (owner, zone, role) = await SetupAsync();
        var username = $"nv{NewCode()}";

        var created = await owner.PostAsJsonAsync("/api/employees", Body($"D {NewCode()}", NewPhone(), zone.Id, account: AccountBlock(username, zone, role)));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var temporaryPassword = body.GetProperty("temporaryPassword").GetString()!;
        var tokens = await LoginTokensAsync(factory.CreateClient(), username, temporaryPassword);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(username, body.GetProperty("employee").GetProperty("account").GetProperty("username").GetString());
        Assert.True(tokens.GetProperty("mustChangePassword").GetBoolean());
        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => (e.After ?? "").Contains(temporaryPassword))));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains(temporaryPassword));
    }

    [Fact]
    public async Task Failing_account_creation_saves_no_employee()
    {
        var (owner, zone, role) = await SetupAsync();
        var taken = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var (name, ownerRightsName) = ($"E {NewCode()}", $"F {NewCode()}");

        var duplicate = await owner.PostAsJsonAsync("/api/employees", Body(name, NewPhone(), zone.Id, account: AccountBlock(taken.Username, zone, role)));
        var ownerRights = await owner.PostAsJsonAsync("/api/employees", Body(ownerRightsName, NewPhone(), zone.Id, account: AccountBlock($"nv{NewCode()}", zone, role, "CooperativeOwner")));

        Assert.Equal("This username is already used by another account. Please enter another one.",
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal("MSG-GEN-08", await ownerRights.ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == name || e.FullName == ownerRightsName)));
        Assert.True(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Account.OwnerRightsRefused")));
    }

    [Fact]
    public async Task End_employment_and_reinstate_with_follow_ups_and_the_later_records_check()
    {
        var (owner, zone, role) = await SetupAsync();
        var created = await (await owner.PostAsJsonAsync("/api/employees", Body($"G {NewCode()}", NewPhone(), zone.Id, account: AccountBlock($"nv{NewCode()}", zone, role))))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("employee").GetProperty("id").GetGuid();
        await factory.UseDbAsync(db => db.AttendanceRecords.Add(new AttendanceRecord { EmployeeId = id, WorkDate = new DateOnly(2026, 10, 20) }));

        var noDate = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { });
        var blocked = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = "2026-10-15" });
        var ended = await (await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = "2026-10-21" })).Content.ReadFromJsonAsync<JsonElement>();
        var again = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = "2026-10-22" });
        var reinstated = await (await owner.PostAsync($"/api/employees/{id}/reinstate", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("MSG-GEN-01", await noDate.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-06", await blocked.ErrorCodeAsync());
        Assert.Equal("Left", ended.GetProperty("employee").GetProperty("status").GetString());
        Assert.Equal("review-lock", ended.GetProperty("followUp").GetProperty("action").GetString());
        Assert.Equal("Employee is Left and cannot be used for this action.",
            (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal("Employed", reinstated.GetProperty("employee").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reinstated.GetProperty("employee").GetProperty("employmentEndDate").ValueKind);
        Assert.Equal("review-access", reinstated.GetProperty("followUp").GetProperty("action").GetString());
    }

    [Fact]
    public async Task Update_with_a_stale_version_is_refused_and_staff_cannot_open_employees()
    {
        var (owner, zone, _) = await SetupAsync();
        var (name, phone) = ($"H {NewCode()}", NewPhone());
        var id = (await (await owner.PostAsJsonAsync("/api/employees", Body(name, phone, zone.Id))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("employee").GetProperty("id").GetGuid();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, await factory.AddRoleAsync($"W {NewCode()}", (PermissionFunctions.LaborWorkers, true, true, true)), null);

        var stale = await owner.PutAsJsonAsync($"/api/employees/{id}", new { fullName = name, phoneNumber = phone, rowVersion = "AAAAAAAAB9E=" });
        var forbidden = await (await factory.LoginAsync(staff.Username)).GetAsync("/api/employees");

        Assert.Equal("MSG06", await stale.ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await forbidden.ErrorCodeAsync());
    }

    private async Task<(HttpClient Owner, FarmingZone Zone, Role Role)> SetupAsync()
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
        var role = await factory.AddRoleAsync($"Z {NewCode()}", (PermissionFunctions.Attendance, true, true, true));
        return (owner, await factory.AddZoneAsync(NewCode()), role);
    }

    private static object AccountBlock(string username, FarmingZone zone, Role role, string? subjectType = null) =>
        new { username, grants = new[] { new { zoneId = zone.Id, roleId = role.Id } }, subjectType };

    private static object Body(string name, string phone, Guid zoneId, bool bank = false, Guid? accountId = null, object? account = null) => new
    {
        fullName = name,
        phoneNumber = phone,
        birthday = "1990-05-01",
        workingZoneIds = new[] { zoneId },
        bankAccount = bank ? new { bankName = "Vietcombank", accountNumber = "1234567890", accountHolderName = "Nguyen Van A" } : null,
        accountId,
        account,
    };
}
