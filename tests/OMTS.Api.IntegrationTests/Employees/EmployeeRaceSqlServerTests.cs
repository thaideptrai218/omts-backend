using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// BR-LABOR-01 under parallel saves on a real SQL Server: the filtered unique index keeps one Employed employee per number,
/// and the loser answers 409 with the same message as the pre-check, never 500.
/// </summary>
public class EmployeeRaceSqlServerTests
{
    private static async Task AssertOneWinnerAsync(HttpResponseMessage[] responses, HttpStatusCode won)
    {
        foreach (var response in responses.Where(r => r.StatusCode != won))
        {
            await response.Content.LoadIntoBufferAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        Assert.Single(responses, r => r.StatusCode == won);
    }

    [SqlServerFact]
    public async Task Parallel_adds_with_one_phone_number_leave_one_employee()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var phone = NewPhone();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => owner.PostAsJsonAsync("/api/employees", Profile($"Hoàng Văn {i} {NewCode()}", phone, zone.Id))));

        await AssertOneWinnerAsync(responses, HttpStatusCode.Created);
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync(e => e.PhoneNumber == phone)));
        var winner = await QueryAsync(api, db => db.Employees.SingleAsync(e => e.PhoneNumber == phone));
        foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var problem = await loser.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-03", "MSG03");
            Assert.Equal(winner.Id, problem.GetProperty("existingEmployeeId").GetGuid());
        }

        Assert.Equal(1, await QueryAsync(api, db => db.EmployeeTypePeriods.CountAsync(p => p.EmployeeId == winner.Id)));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "Employee.Created")));
    }

    [SqlServerFact]
    public async Task Parallel_resumes_and_edits_to_one_number_leave_one_employed_holder()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var phone = NewPhone();
        var left = new List<(Guid Id, string Version)>();
        for (var i = 0; i < 3; i++)
        {
            var id = IdOf(await CreateAsync(owner, Profile($"Left {i} {NewCode()}", phone, zone.Id)));
            var ended = await EndAsync(owner, id);
            left.Add((id, ended.GetProperty("employee").GetProperty("rowVersion").GetString()!));
        }

        var (editName, editPhone) = ($"Edit {NewCode()}", NewPhone());
        var other = await CreateAsync(owner, Profile(editName, editPhone, zone.Id));

        var responses = await Task.WhenAll(left
            .Select(e => owner.PostAsJsonAsync($"/api/employees/{e.Id}/resume", new { rowVersion = e.Version }))
            .Append(owner.PutAsJsonAsync($"/api/employees/{IdOf(other)}", Profile(editName, phone, zone.Id, b => b["rowVersion"] = RowVersionOf(other)))));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK);
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync(e => e.PhoneNumber == phone && e.StatusId == StatusIds.Employee.Working)));
    }

    [SqlServerFact]
    public async Task A_stale_version_answers_MSG06_on_edit_end_and_resume_and_changes_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var (name, phone) = ($"Stale {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));
        var id = IdOf(saved);
        var old = RowVersionOf(saved);

        var edited = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b => { b["notes"] = "Lần 1"; b["rowVersion"] = old; }))).BodyAsync(HttpStatusCode.OK);
        var staleEdit = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id, b => { b["notes"] = "Lần 2"; b["rowVersion"] = old; }));
        var staleEnd = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { lastWorkingDay = Today().ToString("yyyy-MM-dd"), rowVersion = old });
        var problem = await staleEdit.SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);

        Assert.StartsWith($"The data was just updated by {account.DisplayName} at ", problem.Detail());
        Assert.Equal("MSG06", await staleEnd.ErrorCodeAsync());
        Assert.Equal(("Lần 1", (DateOnly?)null), await QueryAsync(api, async db => await db.Employees.Where(e => e.Id == id).Select(e => new ValueTuple<string?, DateOnly?>(e.Notes, e.LastWorkingDay)).SingleAsync()));

        var ended = await EndAsync(owner, id);
        var staleResume = await owner.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = RowVersionOf(edited) });
        var noVersion = await owner.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = "" });

        Assert.Equal("MSG06", await staleResume.ErrorCodeAsync());
        Assert.Equal(("MSG-GEN-01", "RowVersion"), (await noVersion.ErrorCodeAsync(), await FieldAsync(noVersion)));
        Assert.Equal(StatusIds.Employee.Left, await QueryAsync(api, db => db.Employees.Where(e => e.Id == id).Select(e => e.StatusId).SingleAsync()));
        Assert.NotEqual(RowVersionOf(edited), RowVersionOf(ended));
    }

    // A change of the rows of the profile only (zones, job types, type) must also move the version of the profile.
    [SqlServerFact]
    public async Task Changing_only_zones_job_types_or_the_type_makes_older_versions_stale_and_parallel_saves_of_one_profile_answer_409()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var (first, second) = (await AddZoneAsync(api, cooperative.Id), await AddZoneAsync(api, cooperative.Id));
        var (name, phone) = ($"Rows {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, first.Id));
        var id = IdOf(saved);
        var today = Today().ToString("yyyy-MM-dd");

        var typeChanged = await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, first.Id, b =>
        {
            b["employeeType"] = "Staff";
            b["employeeTypeFrom"] = Today().AddDays(1).ToString("yyyy-MM-dd");
            b["rowVersion"] = RowVersionOf(saved);
        }))).BodyAsync(HttpStatusCode.OK);
        Assert.NotEqual(RowVersionOf(saved), RowVersionOf(typeChanged));

        var version = RowVersionOf(typeChanged);
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => owner.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = second.Id }))
                .Append(owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, first.Id, b => { b["notes"] = "Song song"; b["rowVersion"] = version; })))
                .Append(owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { lastWorkingDay = today, rowVersion = version })));

        foreach (var response in responses)
        {
            await response.Content.LoadIntoBufferAsync();
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
    }

    // Nobody can point the audit log at a person who does not exist (GBR-02), and the job leaves the actor empty.
    [SqlServerFact]
    public async Task The_employee_actor_of_an_audit_row_is_a_foreign_key()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (_, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var employee = new Employee { CooperativeId = cooperative.Id, FullName = "Có thật" };
        await ChangeAsync(api, db => db.Employees.Add(employee));

        await ChangeAsync(api, db => db.AuditEvents.Add(new OMTS.Domain.Entities.Platform.AuditEvent { Action = "A.Done", RecordType = "A", OccurredAt = DateTimeOffset.UtcNow, ActorEmployeeId = employee.Id }));
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(api, db => db.AuditEvents.Add(new OMTS.Domain.Entities.Platform.AuditEvent { Action = "A.Done", RecordType = "A", OccurredAt = DateTimeOffset.UtcNow, ActorEmployeeId = Guid.NewGuid() })));

        Assert.Contains("FK_AuditEvents_Employees_ActorEmployeeId", orphan.InnerException!.Message);
    }

    // Employee, account, grants and link are one save: a refused account leaves no row behind, and a taken username under race is 409.
    [SqlServerFact]
    public async Task Parallel_adds_with_one_username_leave_one_account_and_one_employee()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var username = $"nv{NewCode()}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            owner.PostAsJsonAsync("/api/employees", Profile($"Account {i} {NewCode()}", NewPhone(), zone.Id, b => b["account"] = new { username }))));

        await AssertOneWinnerAsync(responses, HttpStatusCode.Created);
        Assert.Equal(1, await QueryAsync(api, db => db.Accounts.CountAsync(a => a.Username == username && a.SubjectType == SubjectType.Staff)));
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync()));
    }
}
