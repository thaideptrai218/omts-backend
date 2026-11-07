using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>Deployment and audit behaviour that needs real SQL Server transactions and constraints.</summary>
public class DeploymentDatabaseTests
{
    private const string TemporaryPassword = "Temp2026ab";

    // Fresh database → readiness red → bootstrap → readiness green → Owner logs in and must change the password.
    [SqlServerFact]
    public async Task Fresh_environment_is_ready_after_the_bootstrap_command()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
        {
            [BootstrapSettings.CooperativeFullNameKey] = "HTX Trung Nam",
            [BootstrapSettings.OwnerDisplayNameKey] = "Chủ nhiệm",
            [BootstrapSettings.OwnerUsernameKey] = "owner",
            [BootstrapSettings.OwnerPhoneNumberKey] = "0912345678",
            [BootstrapSettings.OwnerTemporaryPasswordKey] = TemporaryPassword,
        });
        var client = api.CreateClient();

        var before = await client.GetAsync("/health/ready");
        using (var scope = api.Services.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(false, CancellationToken.None);
            Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        }

        var after = await client.GetAsync("/health/ready");
        var login = await client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = TemporaryPassword });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
    }

    // NFR-02: a change and its audit row are committed together, so a failed save leaves no audit row.
    [SqlServerFact]
    public async Task Failed_save_leaves_no_audit_row()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using var api = new SqlServerApiFactory(database);
        var saved = await AddRoleWithAuditAsync(api, "Harvest team");
        var duplicate = new Role { Name = "Harvest team" };

        // The unique index on Roles.Name refuses it; the unit of work reports it as a duplicate (409 MSG03).
        await Assert.ThrowsAsync<DuplicateRecordException>(() => AddRoleWithAuditAsync(api, duplicate.Name));

        await using var check = database.CreateContext();
        var audited = await check.AuditEvents.Where(e => e.Action == "Role.Created").Select(e => e.RecordId).ToListAsync();
        Assert.Equal([saved], audited);
    }

    private static async Task<Guid> AddRoleWithAuditAsync(SqlServerApiFactory api, string name)
    {
        using var scope = api.Services.CreateScope();
        var services = scope.ServiceProvider;
        var role = new Role { Name = name };
        services.GetRequiredService<OmtsDbContext>().Roles.Add(role);
        services.GetRequiredService<AuditLogService>().Record(new AuditEntry("Role.Created", nameof(Role)) { RecordId = role.Id, After = new { role.Name } });
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return role.Id;
    }
}
