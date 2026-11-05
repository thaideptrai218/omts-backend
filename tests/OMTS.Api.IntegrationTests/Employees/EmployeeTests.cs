using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>LABOR-01.0 and LABOR-01.1: the profile, its approval by the Owner (MSG-LABOR-01), type history and job types.</summary>
public class EmployeeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Owner_creates_and_searches_employees_with_zones_and_masked_bank_audit()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var name = $"Trần Thị {NewCode()}";

        var body = await CreateAsync(owner, Profile(name, NewPhone(), zone.Id, b => b["bankAccount"] = Bank()));
        var id = IdOf(body);
        var found = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={Uri.EscapeDataString(name)}&zoneId={zone.Id}");

        Assert.Equal("Employee profile saved.", body.GetProperty("message").GetString());
        var employee = body.GetProperty("employee");
        Assert.Equal("NGUYEN VAN A", employee.GetProperty("bankAccount").GetProperty("accountHolderName").GetString());
        Assert.Equal(zone.Code, employee.GetProperty("workingZones")[0].GetProperty("code").GetString());
        Assert.Equal("Employed", employee.GetProperty("status").GetString());
        Assert.Equal(1, found.GetProperty("total").GetInt32());
        Assert.Equal("SeasonalWorker", found.GetProperty("items")[0].GetProperty("employeeType").GetString());
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == id && e.Action == "Employee.Created"));
        Assert.Contains("********0001", audit.After);
        Assert.DoesNotContain("999900000001", audit.After);
        Assert.Null(audit.Reason);
    }

    [Fact]
    public async Task A_new_employee_needs_a_name_a_type_and_a_zone_but_not_a_phone_number()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var noPhone = await CreateAsync(owner, Profile($"No phone {NewCode()}", null, zone.Id));
        var blankPhone = await CreateAsync(owner, Profile($"Blank phone {NewCode()}", "  ", zone.Id));
        var refused = await owner.PostAsJsonAsync("/api/employees", new { fullName = " ", phoneNumber = "123", workingZoneIds = Array.Empty<Guid>() });
        var problem = await ProblemAsync(refused);

        Assert.Equal(JsonValueKind.Null, noPhone.GetProperty("employee").GetProperty("phoneNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, blankPhone.GetProperty("employee").GetProperty("phoneNumber").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(["Full name", "Phone number", "Employee type", "Working zones"], problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
    }

    // LABOR-01 step 5: the Zone Manager confirms the approval in person; the Owner is only asked to confirm (MSG-GEN-04, on the client).
    [Fact]
    public async Task A_manager_needs_the_owners_approval_after_the_data_checks_and_the_owner_does_not()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var manager = await ManagerAsync(factory, zone);
        var name = $"Lê Văn {NewCode()}";

        var notApproved = await manager.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id));
        var invalidFirst = await manager.PostAsJsonAsync("/api/employees", Profile(name, "123", zone.Id));
        var approved = await CreateAsync(manager, ApprovedProfile(name, NewPhone(), zone.Id));
        var byOwner = await owner.PostAsJsonAsync("/api/employees", Profile($"Owner {NewCode()}", NewPhone(), zone.Id));

        var problem = await notApproved.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-01", null);
        Assert.Equal($"Has the Cooperative Owner approved, in person, adding {name} as a new employee?", problem.Detail());
        Assert.Equal("MSG-GEN-01", await invalidFirst.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Created, byOwner.StatusCode);
        Assert.Equal(1, await factory.QueryAsync(db => db.Employees.CountAsync(e => e.FullName == name)));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == IdOf(approved) && e.Action == "Employee.Created"));
        Assert.Contains("approved in person", audit.Reason);
    }

    [Fact]
    public async Task The_first_type_applies_from_today_or_the_date_entered()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var byDefault = await CreateAsync(owner, Profile($"Default {NewCode()}", null, zone.Id));
        var dated = await CreateAsync(owner, Profile($"Dated {NewCode()}", null, zone.Id, b => { b["employeeType"] = "Staff"; b["employeeTypeFrom"] = "2026-01-15"; }));

        var firstDay = byDefault.GetProperty("employee").GetProperty("typeHistory")[0];
        Assert.Equal(("SeasonalWorker", Today().ToString("yyyy-MM-dd")), (firstDay.GetProperty("type").GetString(), firstDay.GetProperty("effectiveFrom").GetString()));
        Assert.Equal("Staff", dated.GetProperty("employee").GetProperty("employeeType").GetString());
        Assert.Equal("2026-01-15", dated.GetProperty("employee").GetProperty("typeHistory")[0].GetProperty("effectiveFrom").GetString());
    }

    // BR-LABOR-01: a change during a pay period keeps the days before it; the earlier types are kept.
    [Fact]
    public async Task A_new_type_needs_its_date_and_keeps_the_earlier_types_with_an_audit_row()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Type {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id, b => b["employeeTypeFrom"] = "2026-01-01"));
        var id = IdOf(saved);
        object Edit(string? type, string? from) => Profile(name, phone, zone.Id, b => { b["employeeType"] = type; b["employeeTypeFrom"] = from; b["rowVersion"] = RowVersionOf(saved); });

        var noDate = await owner.PutAsJsonAsync($"/api/employees/{id}", Edit("Staff", null));
        var noType = await owner.PutAsJsonAsync($"/api/employees/{id}", Edit(null, "2026-03-01"));
        Assert.Equal("Employee type date", await FieldAsync(noDate));
        Assert.Equal("Employee type", await FieldAsync(noType));

        var sameType = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Edit("SeasonalWorker", "2026-02-01"))).BodyAsync(HttpStatusCode.OK);
        var noneSent = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Edit(null, null))).BodyAsync(HttpStatusCode.OK);
        Assert.Single(sameType.GetProperty("employee").GetProperty("typeHistory").EnumerateArray());
        Assert.Single(noneSent.GetProperty("employee").GetProperty("typeHistory").EnumerateArray());
        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordId == id && e.Action == "Employee.Updated")));

        var changed = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Edit("Staff", "2026-03-01"))).BodyAsync(HttpStatusCode.OK);

        var history = changed.GetProperty("employee").GetProperty("typeHistory").EnumerateArray().Select(p => (p.GetProperty("type").GetString(), p.GetProperty("effectiveFrom").GetString())).ToList();
        Assert.Equal([("SeasonalWorker", "2026-01-01"), ("Staff", "2026-03-01")], history);
        Assert.Equal("Staff", changed.GetProperty("employee").GetProperty("employeeType").GetString());
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == id && e.Action == "Employee.Updated"));
        Assert.Contains("2026-03-01", audit.After);
        Assert.DoesNotContain("2026-03-01", audit.Before);
    }

    [Fact]
    public async Task Another_type_on_the_date_of_a_period_replaces_it_and_a_future_type_waits_for_its_day()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Type {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id, b => b["employeeTypeFrom"] = "2026-01-01"));
        var id = IdOf(saved);
        var future = Today().AddDays(5).ToString("yyyy-MM-dd");

        await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b => { b["employeeType"] = "Staff"; b["employeeTypeFrom"] = "2026-01-01"; b["rowVersion"] = RowVersionOf(saved); }))).BodyAsync(HttpStatusCode.OK);
        var later = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b => { b["employeeType"] = "SeasonalWorker"; b["employeeTypeFrom"] = future; b["rowVersion"] = RowVersionOf(saved); }))).BodyAsync(HttpStatusCode.OK);

        Assert.Equal("Staff", later.GetProperty("employee").GetProperty("employeeType").GetString());
        var rows = await factory.QueryAsync(db => db.EmployeeTypePeriods.Where(p => p.EmployeeId == id).OrderBy(p => p.EffectiveFrom).Select(p => p.Type).ToListAsync());
        Assert.Equal([EmployeeType.Staff, EmployeeType.SeasonalWorker], rows);
    }

    // BR-LABOR-01: optional; GBR-01: a discontinued job type cannot be chosen, and one already on the profile stays.
    [Fact]
    public async Task Job_types_are_optional_chosen_among_those_in_use_and_a_discontinued_one_stays_on_the_profile()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (harvest, closed) = (await AddJobTypeAsync(factory), await AddJobTypeAsync(factory, j => j.StatusId = StatusIds.Usage.Discontinued));
        var (name, phone) = ($"Job {NewCode()}", NewPhone());

        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id, b => b["jobTypeIds"] = new[] { harvest.Id }));
        var discontinued = await owner.PostAsJsonAsync("/api/employees", Profile($"J {NewCode()}", null, zone.Id, b => b["jobTypeIds"] = new[] { closed.Id }));
        var unknown = await owner.PostAsJsonAsync("/api/employees", Profile($"J {NewCode()}", null, zone.Id, b => b["jobTypeIds"] = new[] { Guid.NewGuid() }));
        var repeated = await owner.PostAsJsonAsync("/api/employees", Profile($"J {NewCode()}", null, zone.Id, b => b["jobTypeIds"] = new[] { harvest.Id, harvest.Id }));

        Assert.Equal(harvest.Name, saved.GetProperty("employee").GetProperty("jobTypes")[0].GetProperty("name").GetString());
        foreach (var refused in new[] { discontinued, unknown, repeated })
        {
            Assert.Equal(("MSG-GEN-01", "Job types"), (await refused.ErrorCodeAsync(), await FieldAsync(refused)));
        }

        await factory.UpdateAsync<JobType>(harvest.Id, j => j.StatusId = StatusIds.Usage.Discontinued);
        var kept = await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, zone.Id, b => { b["jobTypeIds"] = new[] { harvest.Id }; b["notes"] = "Giữ"; b["rowVersion"] = RowVersionOf(saved); }))).BodyAsync(HttpStatusCode.OK);
        Assert.Equal("Discontinued", kept.GetProperty("employee").GetProperty("jobTypes")[0].GetProperty("status").GetString());

        var cleared = await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, zone.Id, b => b["rowVersion"] = RowVersionOf(kept)))).BodyAsync(HttpStatusCode.OK);
        Assert.Empty(cleared.GetProperty("employee").GetProperty("jobTypes").EnumerateArray());
        Assert.False(await factory.QueryAsync(db => db.EmployeeJobTypes.AnyAsync(j => j.EmployeeId == IdOf(saved))));
    }

    // GBR-01: one discontinued after it was given stays, and other edits go on.
    [Fact]
    public async Task A_zone_discontinued_later_does_not_stop_an_edit_and_a_left_employee_cannot_be_edited()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"Zone {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));
        await factory.UpdateAsync<FarmingZone>(zone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);

        var edited = await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, zone.Id, b => { b["notes"] = "Sửa"; b["rowVersion"] = RowVersionOf(saved); }))).BodyAsync(HttpStatusCode.OK);
        await EndAsync(owner, IdOf(saved));
        var left = await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, phone, zone.Id, b => b["rowVersion"] = RowVersionOf(edited)));

        Assert.Equal("Sửa", edited.GetProperty("employee").GetProperty("notes").GetString());
        Assert.Equal("Employee is Left and cannot be used for this action.", (await left.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61")).Detail());
    }
}
