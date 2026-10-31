using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeCertificationRequests;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileSqlServerTests;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>FARM-01 v2 on a real SQL Server: the CooperativeProfileV2 migration both ways, column lengths, real rowversions.</summary>
public class CooperativeProfileV2SqlServerTests
{
    private const string MigrationBefore = "20261031010823_NotificationsV2";

    // The cooperative status is dropped (SRS 27/10) and comes back on Down: Completed with a tax code, else Preliminary.
    [SqlServerFact]
    public async Task Migration_drops_the_cooperative_status_keeps_the_cooperatives_and_down_puts_it_back()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var (preliminary, completed) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, StatusId, CreatedAt) VALUES ({preliminary}, N'HTX A', 201, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Cooperatives (Id, FullName, TaxCode, StatusId, CreatedAt) VALUES ({completed}, N'HTX B', N'0101234567', 202, SYSDATETIMEOFFSET())");

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Cooperatives') AND name = 'StatusId'"));
        Assert.False(await db.Statuses.AnyAsync(s => s.Id == 201 || s.Id == 202 || s.Group == "COOPERATIVE"));
        Assert.False(await db.StatusTransitions.AnyAsync(t => t.Id == 3));
        Assert.Equal(["HTX A", "HTX B"], await db.Cooperatives.OrderBy(c => c.FullName).Select(c => c.FullName).ToListAsync());
        Assert.Equal(0, await db.CooperativeCertifications.CountAsync());

        await migrator.MigrateAsync(MigrationBefore);

        Assert.Equal(201, await db.Database.SqlQuery<int>($"SELECT StatusId AS [Value] FROM Cooperatives WHERE Id = {preliminary}").SingleAsync());
        Assert.Equal(202, await db.Database.SqlQuery<int>($"SELECT StatusId AS [Value] FROM Cooperatives WHERE Id = {completed}").SingleAsync());
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'CooperativeCertifications'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM StatusTransitions WHERE Id = 3"));
    }

    // Every certification field at the longest value FARM-01.1 allows fits its column; a real edit makes the version
    // read before it stale, for a later edit as for the removal.
    [SqlServerFact]
    public async Task Certification_at_maximum_lengths_round_trips_and_a_real_edit_makes_the_old_version_stale()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var created = await owner.CreateCertificationAsync(new CertificationFields
        {
            Name = Vietnamese(200), Number = new string('9', 100), Issuer = Vietnamese(200), ExpiryDate = Today.AddDays(90),
        });
        var id = created.Id();
        var oldVersion = created.Text("rowVersion");

        var edited = (await (await owner.PutAsync($"{Certifications}/{id}", new CertificationFields
        {
            Name = Vietnamese(200), Number = "VG-02", Issuer = Vietnamese(200), ExpiryDate = Today.AddDays(90), File = null, RowVersion = oldVersion,
        }.ToForm())).ReadAsync(HttpStatusCode.OK)).GetProperty("certification");
        var newVersion = edited.Text("rowVersion");
        var staleEdit = await owner.PutAsync($"{Certifications}/{id}", new CertificationFields { File = null, RowVersion = oldVersion }.ToForm());
        var staleRemove = await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(oldVersion)}");
        var stored = await host.QueryAsync(db => db.CooperativeCertifications.AsNoTracking().SingleAsync());
        var file = host.StoredKeys(FileCategories.CooperativeCertifications);
        var removed = await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(newVersion)}");

        Assert.Equal((Vietnamese(200), Vietnamese(200), "VG-02"), (stored.Name, stored.Issuer, stored.Number));
        Assert.NotEqual(oldVersion, newVersion);
        Assert.Equal("MSG06", await staleEdit.ErrorCodeAsync());
        Assert.Equal("MSG06", await staleRemove.ErrorCodeAsync());
        Assert.Single(file);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False(await host.QueryAsync(db => db.CooperativeCertifications.AnyAsync()));
        Assert.Empty(host.StoredKeys(FileCategories.CooperativeCertifications));
        Assert.Equal(3, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == id)));
    }

    /// <summary>A fixed query (no values) returning one number as <c>[Value]</c>.</summary>
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
