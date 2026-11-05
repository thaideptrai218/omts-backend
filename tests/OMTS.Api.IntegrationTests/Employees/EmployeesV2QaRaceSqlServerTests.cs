using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>QA of OMTS-227 under parallel saves on a real SQL Server: 2xx or 4xx, never 500, and the rows stay consistent. Each also runs alone.</summary>
public class EmployeesV2QaRaceSqlServerTests
{
    private static async Task<string> NoServerErrorAsync(IEnumerable<HttpResponseMessage> responses)
    {
        var seen = new List<string>();
        foreach (var response in responses)
        {
            await response.Content.LoadIntoBufferAsync();
            var text = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode < 500, $"{(int)response.StatusCode}: {text}");
            seen.Add($"{(int)response.StatusCode}");
        }

        return string.Join(",", seen.Order());
    }

    [SqlServerFact]
    public async Task Many_parallel_adds_with_one_number_never_answer_500_or_share_it()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);

        for (var round = 0; round < 4; round++)
        {
            var phone = NewPhone();
            var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => owner.PostAsJsonAsync("/api/employees", Profile($"Round {round} {i} {NewCode()}", phone, zone.Id))));

            await NoServerErrorAsync(responses);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Equal(15, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                Assert.Equal("MSG-LABOR-03", await loser.ErrorCodeAsync());
            }

            Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync(e => e.PhoneNumber == phone)));
            Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(phone));
        }
    }

    [SqlServerFact]
    public async Task Parallel_edits_of_different_employees_to_one_number_leave_one_holder()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var profiles = new List<(string Name, JsonElement Saved)>();
        for (var i = 0; i < 8; i++)
        {
            var name = $"Edit {i} {NewCode()}";
            profiles.Add((name, await CreateAsync(owner, Profile(name, NewPhone(), zone.Id))));
        }

        var phone = NewPhone();
        var responses = await Task.WhenAll(profiles.Select(p => owner.PutAsJsonAsync($"/api/employees/{IdOf(p.Saved)}", Profile(p.Name, phone, zone.Id).With("rowVersion", RowVersionOf(p.Saved)))));

        await NoServerErrorAsync(responses);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync(e => e.PhoneNumber == phone)));
    }

    // A swap needs a free number in between: two parallel edits to each other's number must not both pass.
    [SqlServerFact]
    public async Task A_parallel_swap_of_two_numbers_never_leaves_one_number_twice_or_answers_500()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        for (var round = 0; round < 5; round++)
        {
            var (nameA, phoneA, nameB, phoneB) = ($"A {NewCode()}", NewPhone(), $"B {NewCode()}", NewPhone());
            var (a, b) = (await CreateAsync(owner, Profile(nameA, phoneA, zone.Id)), await CreateAsync(owner, Profile(nameB, phoneB, zone.Id)));

            var responses = await Task.WhenAll(
                owner.PutAsJsonAsync($"/api/employees/{IdOf(a)}", Profile(nameA, phoneB, zone.Id).With("rowVersion", RowVersionOf(a))),
                owner.PutAsJsonAsync($"/api/employees/{IdOf(b)}", Profile(nameB, phoneA, zone.Id).With("rowVersion", RowVersionOf(b))));

            await NoServerErrorAsync(responses);
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
            Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync(e => e.PhoneNumber == phoneA)));
            Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync(e => e.PhoneNumber == phoneB)));
        }
    }

    [SqlServerFact]
    public async Task L7_parallel_new_types_on_one_date_leave_one_period_and_answer_msg06()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var (name, phone) = ($"Type {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id));
        var id = IdOf(saved);
        var from = Today().AddDays(3).ToString("yyyy-MM-dd");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("employeeType", "Staff").With("employeeTypeFrom", from).With("rowVersion", RowVersionOf(saved)))));

        await NoServerErrorAsync(responses);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(2, await QueryAsync(api, db => db.EmployeeTypePeriods.CountAsync(p => p.EmployeeId == id)));
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal("MSG06", await loser.ErrorCodeAsync());
        }
    }

    // LABOR-01.2: "A zone the employee already has changes nothing and answers the same" (client list).
    [SqlServerFact]
    public async Task L7_parallel_add_my_zone_of_one_zone_keeps_one_row_and_answers_msg06_at_worst()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var (zoneA, zoneB) = (await AddZoneAsync(api, cooperative.Id), await AddZoneAsync(api, cooperative.Id));
        var id = IdOf(await CreateAsync(owner, Profile($"Zone {NewCode()}", NewPhone(), zoneA.Id)));

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => owner.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = zoneB.Id })));

        await NoServerErrorAsync(responses);
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await QueryAsync(api, db => db.EmployeeWorkingZones.CountAsync(w => w.EmployeeId == id && w.ZoneId == zoneB.Id)));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "Employee.WorkingZoneAdded")));
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal("MSG06", await loser.ErrorCodeAsync());
        }
    }

    [SqlServerFact]
    public async Task Everything_at_once_on_one_employee_answers_2xx_or_4xx_and_stays_consistent()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var (zoneA, zoneB) = (await AddZoneAsync(api, cooperative.Id), await AddZoneAsync(api, cooperative.Id));
        var job = api.Services.GetRequiredService<EmploymentEndJob>();
        for (var round = 0; round < 4; round++)
        {
            var (name, phone) = ($"All {NewCode()}", NewPhone());
            var saved = await CreateAsync(owner, Profile(name, phone, zoneA.Id));
            var (id, version) = (IdOf(saved), RowVersionOf(saved));
            var yesterday = Today().AddDays(-1).ToString("yyyy-MM-dd");
            var jobRun = job.RunOnceAsync(Today());

            var responses = await Task.WhenAll(
                owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zoneA.Id).With("notes", "Song song").With("rowVersion", version)),
                owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { lastWorkingDay = yesterday, rowVersion = version }),
                owner.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = zoneB.Id }),
                owner.PostAsJsonAsync($"/api/employees/{id}/account", new { username = $"nv{NewCode()}" }),
                owner.PostAsJsonAsync($"/api/employees/{id}/account", new { username = $"nv{NewCode()}" }),
                owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, null, zoneA.Id).With("rowVersion", version)));
            await jobRun;

            await NoServerErrorAsync(responses);
            var employee = await QueryAsync(api, db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == id));
            Assert.Equal(employee.AccountId is null ? 0 : 1, await QueryAsync(api, db => db.Accounts.CountAsync(a => a.PhoneNumber == phone && a.SubjectType == SubjectType.Staff)));
        }
    }

    [SqlServerFact]
    public async Task Parallel_accounts_for_one_employee_leave_one_account()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, Profile($"Acc {NewCode()}", phone, zone.Id)));

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => owner.PostAsJsonAsync($"/api/employees/{id}/account", new { username = $"nv{NewCode()}" })));

        await NoServerErrorAsync(responses);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(1, await QueryAsync(api, db => db.Accounts.CountAsync(a => a.PhoneNumber == phone)));
    }

    // LABOR-01.5.E2 when the other account appears at that same moment: 409, nothing half saved.
    [SqlServerFact]
    public async Task An_add_with_an_account_racing_an_account_with_that_number_saves_neither_half()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        for (var round = 0; round < 5; round++)
        {
            var (phone, name) = (NewPhone(), $"Half {NewCode()}");

            var responses = await Task.WhenAll(
                owner.PostAsJsonAsync("/api/employees", Profile(name, phone, zone.Id).With("account", new { username = $"nv{NewCode()}" })),
                owner.PostAsJsonAsync("/api/accounts", new { displayName = "Tài khoản", username = $"nv{NewCode()}", phoneNumber = phone }));

            await NoServerErrorAsync(responses);
            Assert.Equal(1, await QueryAsync(api, db => db.Accounts.CountAsync(a => a.PhoneNumber == phone)));
            Assert.Equal(responses[0].StatusCode == HttpStatusCode.Created ? 1 : 0, await QueryAsync(api, db => db.Employees.CountAsync(e => e.FullName == name)));
        }
    }

    // The daily job read a row, someone saves it, the job's save loses (BR-LABOR-01: Left from the next day).
    [SqlServerFact]
    public async Task L2_a_job_that_loses_to_an_edit_still_makes_the_employee_left_on_that_day()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var today = Today();
        var id = IdOf(await CreateAsync(owner, Profile($"Job {NewCode()}", NewPhone(), zone.Id)));
        await EndAsync(owner, id, today);
        var edit = new EditOnce(database, id);
        await using var racing = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new EditBeforeSaveUnitOfWork(provider.GetRequiredService<OmtsDbContext>(), edit));
        }));

        await racing.Services.GetRequiredService<EmploymentEndJob>().RunOnceAsync(today.AddDays(1));

        Assert.True(edit.Done);
        Assert.Equal(StatusIds.Employee.Left, await QueryAsync(api, db => db.Employees.Where(e => e.Id == id).Select(e => e.StatusId).SingleAsync()));
    }

    /// <summary>Another session saves the employee once, between the read of the job and its save.</summary>
    private sealed class EditOnce(SqlServerTestDatabase database, Guid employeeId)
    {
        public bool Done { get; private set; }

        public async Task RunAsync()
        {
            if (Done)
            {
                return;
            }

            Done = true;
            await using var other = database.CreateContext();
            await other.Database.ExecuteSqlRawAsync("UPDATE Employees SET Notes = N'Sửa cùng lúc' WHERE Id = {0}", employeeId);
        }
    }

    private sealed class EditBeforeSaveUnitOfWork(OmtsDbContext db, EditOnce edit) : IUnitOfWork
    {
        private readonly UnitOfWork _inner = new(db);

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await edit.RunAsync();
            return await _inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => _inner.ReloadAsync(entity, cancellationToken);

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => _inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => _inner.MarkChanged(entity);
    }
}
