using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A uniquely named database on the server from <see cref="SqlServerFactAttribute.VariableName"/>,
/// dropped when the test ends.
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private SqlServerTestDatabase(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    public static SqlServerTestDatabase Create()
    {
        var server = Environment.GetEnvironmentVariable(SqlServerFactAttribute.VariableName)
            ?? throw new InvalidOperationException($"{SqlServerFactAttribute.VariableName} is not set.");
        var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = $"omts_test_{Guid.NewGuid():N}" };
        return new SqlServerTestDatabase(builder.ConnectionString);
    }

    public OmtsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>
    /// What <c>Database.MigrateAsync()</c> gives a new database, restored from one migrated template of this process
    /// (<see cref="SqlServerMigratedTemplate"/>) instead of running every migration again. Tests of a migration itself
    /// (to or from a given migration, its Up and Down) keep using the migrator.
    /// </summary>
    public async Task MigrateToLatestAsync()
    {
        if (!await SqlServerMigratedTemplate.TryRestoreAsync(ConnectionString))
        {
            // The database exists already (a second call, or built some other way): the migrator brings it up to date.
            await using var db = CreateContext();
            await db.Database.MigrateAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}
