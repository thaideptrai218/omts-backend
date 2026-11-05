using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// The moment a parallel test only hits by chance: another session saves an Employed employee with the number after the
/// pre-check and before this save. The filtered unique index refuses this save, which answers the same MSG-LABOR-03.
/// </summary>
public class EmployeePhoneIndexSqlServerTests
{
    [SqlServerFact]
    public async Task A_number_taken_after_the_check_answers_msg_labor_03_on_add_edit_and_resume()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var race = new PhoneRace(database, cooperative.Id);
        await using var racing = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new RacingUnitOfWork(provider.GetRequiredService<OmtsDbContext>(), race));
        }));
        var client = racing.CreateClient();
        client.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;

        var (editName, editPhone) = ($"Sửa {NewCode()}", NewPhone());
        var toEdit = await CreateAsync(owner, Profile(editName, editPhone, zone.Id));
        var left = await EndAsync(owner, IdOf(await CreateAsync(owner, Profile($"Nghỉ {NewCode()}", NewPhone(), zone.Id))));
        var requests = new Func<string, Task<HttpResponseMessage>>[]
        {
            phone => client.PostAsJsonAsync("/api/employees", Profile($"Mới {NewCode()}", phone, zone.Id)),
            phone => client.PutAsJsonAsync($"/api/employees/{IdOf(toEdit)}", Profile(editName, phone, zone.Id, b => b["rowVersion"] = RowVersionOf(toEdit))),
            phone => client.PostAsJsonAsync($"/api/employees/{IdOf(left)}/resume", new { rowVersion = RowVersionOf(left), phoneNumber = phone }),
        };

        foreach (var request in requests)
        {
            var phone = NewPhone();
            var winner = race.TakeNext(phone);

            var problem = await (await request(phone)).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-03", "MSG03");

            Assert.Equal(winner.Id, problem.GetProperty("existingEmployeeId").GetGuid());
            Assert.Equal([winner.Id], await QueryAsync(api, db => db.Employees.Where(e => e.PhoneNumber == phone).Select(e => e.Id).ToListAsync()));
        }

        Assert.Equal(editPhone, await QueryAsync(api, db => db.Employees.Where(e => e.Id == IdOf(toEdit)).Select(e => e.PhoneNumber).SingleAsync()));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordId == IdOf(left) && e.Action == "Employee.StatusChanged")));
    }

    /// <summary>The other session: once asked for a number, it saves its employee on its own connection just before the next save with that number.</summary>
    private sealed class PhoneRace(SqlServerTestDatabase database, Guid cooperativeId)
    {
        private string? _phone;
        private Employee? _winner;

        public Employee TakeNext(string phone)
        {
            _phone = phone;
            return _winner = new Employee { CooperativeId = cooperativeId, FullName = $"Winner {NewCode()}", PhoneNumber = phone };
        }

        public async Task RunIfDueAsync(OmtsDbContext db)
        {
            var saving = db.ChangeTracker.Entries<Employee>().Any(e => e.State is EntityState.Added or EntityState.Modified && e.Entity.PhoneNumber == _phone);
            if (_winner is not { } winner || !saving)
            {
                return;
            }

            _winner = null;
            await using var other = database.CreateContext();
            other.Employees.Add(winner);
            await other.SaveChangesAsync();
        }
    }

    private sealed class RacingUnitOfWork(OmtsDbContext db, PhoneRace race) : IUnitOfWork
    {
        private readonly UnitOfWork _inner = new(db, new OMTS.Infrastructure.Messaging.PushOutbox());

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await race.RunIfDueAsync(db);
            return await _inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => _inner.ReloadAsync(entity, cancellationToken);

        public void DiscardChanges() => _inner.DiscardChanges();

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => _inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => _inner.MarkChanged(entity);
    }
}
