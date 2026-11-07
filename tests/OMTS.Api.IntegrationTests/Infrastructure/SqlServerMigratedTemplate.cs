using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// One database migrated to the latest migration per test process, kept as a backup file on the server. Hundreds of
/// test databases each running every migration, in parallel on one server, made CI slow and its commands time out;
/// restoring this backup gives each of them the same schema and seed rows for a fraction of that work.
/// </summary>
internal static class SqlServerMigratedTemplate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Template? _template;

    /// <summary>
    /// Restores the template as the database of <paramref name="connectionString"/>; false, and nothing done, when
    /// that database exists already.
    /// </summary>
    public static async Task<bool> TryRestoreAsync(string connectionString)
    {
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        await using var connection = await OpenMasterAsync(connectionString);
        if (await ExistsAsync(connection, name))
        {
            return false;
        }

        var template = await GetAsync(connectionString);
        await using var restore = connection.CreateCommand();
        restore.Parameters.AddWithValue("@name", name);
        restore.Parameters.AddWithValue("@backup", template.BackupFile);
        restore.Parameters.AddWithValue("@data", template.DataLogicalName);
        restore.Parameters.AddWithValue("@dataFile", Combine(template.DataDirectory, $"{name}.mdf"));
        restore.Parameters.AddWithValue("@log", template.LogLogicalName);
        restore.Parameters.AddWithValue("@logFile", Combine(template.LogDirectory, $"{name}_log.ldf"));
        // The files get the new database's name; the logical names stay those of the template, which nothing reads.
        restore.CommandText = "RESTORE DATABASE @name FROM DISK = @backup WITH MOVE @data TO @dataFile, MOVE @log TO @logFile";
        await restore.ExecuteNonQueryAsync();
        return true;
    }

    private static async Task<Template> GetAsync(string connectionString)
    {
        await Gate.WaitAsync();
        try
        {
            // Built once, by the first test that needs it; a failure is not kept, so the next test tries again.
            return _template ??= await CreateAsync(connectionString);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Template> CreateAsync(string connectionString)
    {
        var name = $"omts_template_{Guid.NewGuid():N}";
        var templateConnection = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name }.ConnectionString;
        await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(templateConnection).Options);
        try
        {
            // Created like every test database was (from model, by the migrator), so a restore keeps its options:
            // recovery model, delayed durability, read committed snapshot.
            await db.Database.MigrateAsync();

            await using var connection = await OpenMasterAsync(connectionString);
            var (backupDirectory, dataDirectory, logDirectory) = await DirectoriesAsync(connection);
            var (dataLogicalName, logLogicalName) = await LogicalNamesAsync(connection, name);
            var backupFile = Combine(backupDirectory, $"{name}.bak");

            await using var backup = connection.CreateCommand();
            backup.Parameters.AddWithValue("@name", name);
            backup.Parameters.AddWithValue("@backup", backupFile);
            backup.CommandText = "BACKUP DATABASE @name TO DISK = @backup WITH COPY_ONLY, INIT";
            await backup.ExecuteNonQueryAsync();

            AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteBackupFile(connectionString, backupFile);
            return new Template(backupFile, dataLogicalName, logLogicalName, dataDirectory, logDirectory);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    /// <summary>The server's own folders, so this works on the Linux container of CI and on a local Windows instance.</summary>
    private static async Task<(string Backup, string Data, string Log)> DirectoriesAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)),
                   CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)),
                   CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var data = reader.GetString(1);
        return (reader.IsDBNull(0) ? data : reader.GetString(0), data, reader.IsDBNull(2) ? data : reader.GetString(2));
    }

    private static async Task<(string Data, string Log)> LogicalNamesAsync(SqlConnection connection, string database)
    {
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@name", database);
        command.CommandText = "SELECT name, type_desc FROM sys.master_files WHERE database_id = DB_ID(@name)";
        var files = new List<(string Name, string Type)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                files.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        // A database created from model has one data file and one log file; anything else needs more MOVE clauses.
        if (files.Count != 2 || files.Count(f => f.Type == "ROWS") != 1 || files.Count(f => f.Type == "LOG") != 1)
        {
            throw new InvalidOperationException($"The template database has unexpected files: {string.Join(", ", files)}.");
        }

        return (files.Single(f => f.Type == "ROWS").Name, files.Single(f => f.Type == "LOG").Name);
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, string database)
    {
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@name", database);
        command.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END";
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task<SqlConnection> OpenMasterAsync(string connectionString)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>A path on the server, which may not be the OS of this process: its own separator is kept.</summary>
    private static string Combine(string directory, string file)
    {
        var separator = directory.Contains('/') ? '/' : '\\';
        return $"{directory.TrimEnd('/', '\\')}{separator}{file}";
    }

    /// <summary>Best effort: the file is on the server (inside the container on CI), so the server deletes it.</summary>
    private static void DeleteBackupFile(string connectionString, string backupFile)
    {
        try
        {
            using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                ConnectTimeout = 5,
            }.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.Parameters.AddWithValue("@backup", backupFile);
            command.CommandText = "EXEC master.sys.xp_delete_file 0, @backup";
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // A file left behind costs disk space, never a test.
        }
    }

    private sealed record Template(string BackupFile, string DataLogicalName, string LogLogicalName, string DataDirectory, string LogDirectory);
}
