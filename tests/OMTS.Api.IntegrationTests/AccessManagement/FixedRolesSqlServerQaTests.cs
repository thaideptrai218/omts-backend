using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>
/// QA of the fixed roles on real SQL Server (OMTS-216 PR 0.3): the <c>FixedRoles</c> migration on a database with the
/// old role rows (Up, Down, Up again), the bootstrap on such a database, and FARM-05 grants and revokes racing.
/// </summary>
public class FixedRolesSqlServerQaTests
{
    // Old rows: the two default roles, a role the Owner made (with stored rights on supplier payments) and a discontinued
    // one. Up codes the default rows, gives the others unique placeholder codes that grant nothing, and builds the unique
    // index; Down restores the old columns and tables; Up runs again on the rolled back database.
    [SqlServerFact]
    public async Task FixedRoles_migration_codes_the_default_rows_and_rolls_back_and_forward()
    {
        await using var database = SqlServerTestDatabase.Create();
        var ids = new OldData();
        await using (var db = database.CreateContext())
        {
            var (before, fixedRoles) = Migrations(db);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(before);
            await ids.InsertAsync(db);

            await migrator.MigrateAsync(fixedRoles);

            var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id);
            Assert.Equal(FixedRoles.ZoneManagerCode, roles[ids.ZoneManagerRole].Code);
            Assert.Equal(FixedRoles.AccountantCode, roles[ids.AccountantRole].Code);
            foreach (var old in new[] { ids.CustomRole, ids.DiscontinuedRole })
            {
                Assert.StartsWith("LEGACY-", roles[old].Code, StringComparison.Ordinal);
                Assert.InRange(roles[old].Code.Length, 8, Role.CodeMaxLength);
                Assert.Null(FixedRoles.FindByCode(roles[old].Code));
            }

            Assert.Equal(roles.Count, roles.Values.Select(r => r.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM sys.indexes WHERE name = 'IX_Roles_Code' AND object_id = OBJECT_ID('Roles') AND is_unique = 1"));
            Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM sys.tables WHERE name = 'RolePermissions'"));
            Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM sys.columns WHERE (object_id = OBJECT_ID('AccessGrants') AND name IN ('EffectiveFrom', 'EffectiveTo')) OR (object_id = OBJECT_ID('Roles') AND name = 'StatusId')"));
            Assert.Equal(4, await db.AccessGrants.CountAsync());

            // The unique code, case-insensitive like the lookups (FixedRoles.FindByCode).
            db.Roles.Add(new Role { Code = "zone_manager", Name = $"Copy {NewCode()}" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            await migrator.MigrateAsync(before);
            Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM sys.columns WHERE object_id = OBJECT_ID('Roles') AND name = 'Code'"));
            Assert.Equal(4, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM Roles WHERE StatusId = 101"));
            Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM RolePermissions"));
            Assert.Equal(4, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM AccessGrants WHERE EffectiveFrom = '0001-01-01' AND EffectiveTo IS NULL"));

            await migrator.MigrateAsync(fixedRoles);
            Assert.Equal(
                [FixedRoles.AccountantCode, FixedRoles.ZoneManagerCode],
                (await db.Roles.Where(r => !r.Code.StartsWith("LEGACY-")).Select(r => r.Code).ToListAsync()).Order());
            await db.Database.MigrateAsync();
        }

        // Notification recipients follow the codes too: the old role's stored rights on supplier payments are gone, the
        // Accountant pair of one zone reaches supplier payments of every zone (BR-FARM-08), revoked pairs never count.
        await using var api = new SqlServerApiFactory(database);
        using var scope = api.Services.CreateScope();
        var recipients = scope.ServiceProvider.GetRequiredService<IZoneRecipientQuery>();
        var otherZone = await AddZoneAsync(database);
        var payments = await recipients.GetRecipientsAsync(otherZone, PermissionFunctions.SupplierPayments, PermissionAction.View, CancellationToken.None);
        var water = await recipients.GetRecipientsAsync(ids.Zone, PermissionFunctions.WaterMeasurements, PermissionAction.Create, CancellationToken.None);
        Assert.Equal([ids.Accountant], payments);
        Assert.Equal([ids.ZoneManager], water);
    }

    // Readiness check and seeder find the fixed roles by code: on a database migrated from old rows where the Owner had
    // renamed the default Zone Manager row, the bootstrap adds a ZONE_MANAGER row, keeps the Accountant row and is ready.
    [SqlServerFact]
    public async Task Bootstrap_on_a_migrated_database_adds_the_missing_fixed_role_by_code()
    {
        await using var database = SqlServerTestDatabase.Create();
        var ids = new OldData { ZoneManagerName = "Field lead", WithOwner = true };
        await using (var db = database.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(Migrations(db).Before);
            await ids.InsertAsync(db);
        }

        await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
        {
            [BootstrapSettings.CooperativeFullNameKey] = "HTX Trung Nam",
            [BootstrapSettings.OwnerDisplayNameKey] = "Chủ nhiệm",
            [BootstrapSettings.OwnerUsernameKey] = $"owner{NewCode()}",
            [BootstrapSettings.OwnerPhoneNumberKey] = NewPhone(),
            [BootstrapSettings.OwnerTemporaryPasswordKey] = NewPassword(),
        });
        using (var scope = api.Services.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(false, CancellationToken.None);
            Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        }

        await using var check = database.CreateContext();
        var roles = await check.Roles.AsNoTracking().ToListAsync();
        Assert.Equal(ids.AccountantRole, roles.Single(r => r.Code == FixedRoles.AccountantCode).Id);
        var zoneManager = roles.Single(r => r.Code == FixedRoles.ZoneManagerCode);
        Assert.NotEqual(ids.ZoneManagerRole, zoneManager.Id);
        Assert.Equal(FixedRoles.ZoneManager.Name, zoneManager.Name);
        Assert.StartsWith("LEGACY-", roles.Single(r => r.Id == ids.ZoneManagerRole).Code, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/health/ready")).StatusCode);
    }

    // FARM-05.0.E1: a double click on "Add pair" must not create a copy. Every answer is 201 or 409 MSG-GEN-02, never 500,
    // and one Effective pair is stored.
    [SqlServerFact]
    public async Task Parallel_grants_of_one_zone_pair_create_one_pair()
    {
        var (database, api, owner, zoneId) = await HostAsync();
        await using var d = database;
        await using var a = api;
        var staff = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId, role = FixedRoles.ZoneManagerCode })));

        await AssertOneCreatedAsync(responses);
        Assert.Equal(1, await EffectivePairsAsync(database, staff.Id));
    }

    // BR-FARM-08 (A): the same for All zones pairs.
    [SqlServerFact]
    public async Task Parallel_grants_of_one_all_zones_pair_create_one_pair()
    {
        var (database, api, owner, _) = await HostAsync();
        await using var d = database;
        await using var a = api;
        var staff = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId = (Guid?)null, role = FixedRoles.AccountantCode })));

        await AssertOneCreatedAsync(responses);
        Assert.Equal(1, await EffectivePairsAsync(database, staff.Id));
    }

    // FARM-05.1: two Owners' tabs revoke the same pair. One revoke wins and is audited once; the other answers 409, never
    // 500, and does not overwrite the reason.
    [SqlServerFact]
    public async Task Parallel_revokes_of_one_pair_revoke_it_once()
    {
        var (database, api, owner, zoneId) = await HostAsync();
        await using var d = database;
        await using var a = api;
        var staff = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var granted = await owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants", new { zoneId, role = FixedRoles.ZoneManagerCode });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        var pair = (await granted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(i => owner.PostAsJsonAsync($"/api/accounts/{staff.Id}/grants/{pair}/revoke", new { reason = $"Reason {i}" })));

        var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r != winner), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var reason = (await winner.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("revokeReason").GetString();
        await using var db = database.CreateContext();
        var stored = await db.AccessGrants.SingleAsync(g => g.Id == pair);
        Assert.Equal((StatusIds.AccessGrant.Revoked, reason), (stored.StatusId, stored.RevokeReason));
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.RecordId == pair && e.Reason != null));
    }

    private static async Task AssertOneCreatedAsync(HttpResponseMessage[] responses)
    {
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-GEN-02", await refused.ErrorCodeAsync());
        }
    }

    private static async Task<int> EffectivePairsAsync(SqlServerTestDatabase database, Guid accountId)
    {
        await using var db = database.CreateContext();
        return await db.AccessGrants.CountAsync(g => g.AccountId == accountId && g.StatusId == StatusIds.AccessGrant.Effective);
    }

    /// <summary>A migrated database with the fixed role rows and one zone, and a logged-in Owner.</summary>
    private static async Task<(SqlServerTestDatabase Database, SqlServerApiFactory Api, HttpClient Owner, Guid ZoneId)> HostAsync()
    {
        var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var api = new SqlServerApiFactory(database);
        await api.EnsureFixedRolesAsync();
        var account = await api.AddAccountAsync(NewCode(), Password);
        var owner = api.CreateClient();
        var tokens = await LoginTokensAsync(owner, account.Username);
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (database, api, owner, await AddZoneAsync(database));
    }

    private static async Task<Guid> AddZoneAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        var cooperative = await db.Cooperatives.FirstOrDefaultAsync() ?? new Cooperative { FullName = "HTX Trung Nam" };
        var zone = new FarmingZone { Cooperative = cooperative, Code = NewCode(), Name = "Zone SQL" };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        return zone.Id;
    }

    private static (string Before, string FixedRoles) Migrations(DbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_FixedRoles", StringComparison.Ordinal));
        return (all[index - 1], all[index]);
    }

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    /// <summary>Rows written with the schema before the migration: roles, stored rights and pairs of three staff accounts.</summary>
    private sealed class OldData
    {
        public string ZoneManagerName { get; init; } = "Zone Manager";

        /// <summary>Also an active Cooperative Owner, as a deployed database has.</summary>
        public bool WithOwner { get; init; }
        public Guid Zone { get; } = Guid.NewGuid();
        public Guid ZoneManagerRole { get; } = Guid.NewGuid();
        public Guid AccountantRole { get; } = Guid.NewGuid();
        public Guid CustomRole { get; } = Guid.NewGuid();
        public Guid DiscontinuedRole { get; } = Guid.NewGuid();
        public Guid ZoneManager { get; } = Guid.NewGuid();
        public Guid Accountant { get; } = Guid.NewGuid();
        public Guid CustomHolder { get; } = Guid.NewGuid();

        public async Task InsertAsync(OmtsDbContext db)
        {
            var (cooperative, accountantZone) = (Guid.NewGuid(), Guid.NewGuid());
            await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, StatusId, CreatedAt) VALUES ({cooperative}, N'HTX', 201, SYSDATETIMEOFFSET())");
            foreach (var zone in new[] { Zone, accountantZone })
            {
                await db.Database.ExecuteSqlAsync($"INSERT INTO FarmingZones (Id, CooperativeId, Code, Name, StatusId, CreatedAt) VALUES ({zone}, {cooperative}, {NewCode()}, N'Zone', 101, SYSDATETIMEOFFSET())");
            }

            var accounts = new[] { (ZoneManager, "Staff"), (Accountant, "Staff"), (CustomHolder, "Staff") };
            foreach (var (account, subjectType) in WithOwner ? accounts.Append((Guid.NewGuid(), "CooperativeOwner")).ToArray() : accounts)
            {
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt) VALUES ({account}, {NewCode()}, N'Old', N'x', {subjectType}, 301, 0, 0, SYSDATETIMEOFFSET())");
            }

            foreach (var (id, name, status) in new[] { (ZoneManagerRole, ZoneManagerName, 101), (AccountantRole, "Accountant", 101), (CustomRole, "Harvest team", 101), (DiscontinuedRole, "Night guard", 102) })
            {
                await db.Database.ExecuteSqlAsync($"INSERT INTO Roles (Id, Name, StatusId, CreatedAt) VALUES ({id}, {name}, {status}, SYSDATETIMEOFFSET())");
            }

            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO RolePermissions (RoleId, FunctionCode, CanCreate, CanDelete, CanEdit, CanView, DataScope, OtherPermissions) VALUES ({CustomRole}, N'SUPPLIER_PAYMENTS', 1, 0, 0, 1, N'AssignedZone', N'[]')");
            foreach (var (account, role, zone, status) in new (Guid Account, Guid Role, Guid? Zone, int Status)[]
            {
                (ZoneManager, ZoneManagerRole, Zone, 501),
                (Accountant, AccountantRole, accountantZone, 501),
                (CustomHolder, CustomRole, null, 501),
                (Accountant, AccountantRole, null, 502),
            })
            {
                var id = Guid.NewGuid();
                await (zone is { } zoneId
                    ? db.Database.ExecuteSqlAsync(
                        $"INSERT INTO AccessGrants (Id, AccountId, RoleId, ZoneId, ScopeType, EffectiveFrom, GrantedByAccountId, StatusId, CreatedAt) VALUES ({id}, {account}, {role}, {zoneId}, N'SpecificZone', '2026-01-01', {account}, {status}, SYSDATETIMEOFFSET())")
                    : db.Database.ExecuteSqlAsync(
                        $"INSERT INTO AccessGrants (Id, AccountId, RoleId, ZoneId, ScopeType, EffectiveFrom, GrantedByAccountId, StatusId, CreatedAt) VALUES ({id}, {account}, {role}, NULL, N'AllZones', '2026-01-01', {account}, {status}, SYSDATETIMEOFFSET())"));
            }
        }
    }
}
