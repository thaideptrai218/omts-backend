using System.Net.Http.Json;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Persistence;

public class MigrationTests
{
    // OMTS-208 done criteria: a fresh database built from the migrations has the status catalog, keeps the
    // schema rules (no cascade delete) and the API starts on it.
    [SqlServerFact]
    public async Task Fresh_database_from_migrations_has_the_status_catalog_and_serves_the_api()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Status))!.GetSeedData().Count(), await db.Statuses.CountAsync());
            Assert.Equal(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StatusTransition))!.GetSeedData().Count(), await db.StatusTransitions.CountAsync());
            var cascadingKeys = await db.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE delete_referential_action <> 0")
                .SingleAsync();
            Assert.Equal(0, cascadingKeys);
        }

        // Added last, so it wins over a developer's user-secrets.
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:OmtsDb"] = database.ConnectionString, ["Jobs:OrphanFileCleanup:Enabled"] = "false" })));
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { identifier = "nobody", password = "Secret123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}
