using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Persistence;

/// <summary>
/// Most database tests get their database from <see cref="SqlServerTestDatabase.MigrateToLatestAsync"/>, a restore of one
/// migrated template: it must be the database the migrator builds, down to the options, the seed rows and the history.
/// </summary>
public class MigratedTemplateRestoreTests
{
    // Types BINARY_CHECKSUM cannot take; rowversion differs per database by design.
    private static readonly string[] NotCompared = ["timestamp", "xml", "text", "ntext", "image", "geography", "geometry", "sql_variant", "hierarchyid"];

    // One query per aspect; the columns of a row are joined in C# (catalog views mix collations, so SQL cannot concatenate
    // them). Names the server generates (an unnamed default or constraint) differ between any two databases: left out.
    private static readonly string[] ShapeQueries =
    [
        """
        SELECT 'database', recovery_model_desc, delayed_durability_desc, is_read_committed_snapshot_on, snapshot_isolation_state_desc,
            compatibility_level, collation_name, page_verify_option_desc, is_auto_close_on, is_auto_shrink_on
        FROM sys.databases WHERE database_id = DB_ID()
        """,
        """
        SELECT 'column', OBJECT_SCHEMA_NAME(c.object_id), o.name, c.name, c.column_id, TYPE_NAME(c.user_type_id), c.max_length, c.precision,
            c.scale, c.is_nullable, c.is_identity, c.collation_name, cc.definition, CASE WHEN dc.is_system_named = 0 THEN dc.name END, dc.definition
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U', 'V')
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        """,
        """
        SELECT 'index', o.name, CASE WHEN kc.is_system_named = 1 THEN '(generated)' ELSE i.name END, i.type_desc, i.is_unique,
            i.is_primary_key, i.filter_definition, COL_NAME(ic.object_id, ic.column_id), ic.key_ordinal, ic.is_included_column, ic.is_descending_key
        FROM sys.indexes i
        JOIN sys.objects o ON o.object_id = i.object_id AND o.is_ms_shipped = 0
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
        """,
        """
        SELECT 'foreign key', CASE WHEN is_system_named = 1 THEN '(generated)' ELSE name END, OBJECT_NAME(parent_object_id),
            OBJECT_NAME(referenced_object_id), delete_referential_action_desc, update_referential_action_desc
        FROM sys.foreign_keys
        """,
        "SELECT 'check', OBJECT_NAME(parent_object_id), CASE WHEN is_system_named = 1 THEN '(generated)' ELSE name END, definition FROM sys.check_constraints",
        "SELECT 'module', OBJECT_NAME(object_id), definition FROM sys.sql_modules",
        "SELECT 'sequence', name, CAST(start_value AS nvarchar(100)), CAST(increment AS nvarchar(100)) FROM sys.sequences",
        "SELECT 'migration', MigrationId, ProductVersion FROM [__EFMigrationsHistory]",
    ];

    [SqlServerFact]
    public async Task A_restored_database_is_the_one_the_migrator_builds()
    {
        await using var migrated = SqlServerTestDatabase.Create();
        await using (var db = migrated.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using var restored = SqlServerTestDatabase.Create();
        await restored.MigrateToLatestAsync();

        var (migratedShape, restoredShape) = (await ShapeAsync(migrated), await ShapeAsync(restored));
        Assert.Contains(migratedShape, line => line.StartsWith("migration|", StringComparison.Ordinal));
        Assert.Contains(migratedShape, line => line.StartsWith("rows|[dbo].[Statuses]|", StringComparison.Ordinal) && !line.Contains("|0|"));
        Assert.Equal(migratedShape, restoredShape);

        // The host or a bootstrap migrating at start finds nothing to do; a second call on the same database neither.
        await restored.MigrateToLatestAsync();
        await using var check = restored.CreateContext();
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        Assert.Equal(restoredShape, await ShapeAsync(restored));
    }

    /// <summary>Schema, options and history as sorted lines, and the row count and checksum of every table.</summary>
    private static async Task<List<string>> ShapeAsync(SqlServerTestDatabase database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var lines = new List<string>();
        foreach (var query in ShapeQueries)
        {
            lines.AddRange(await LinesAsync(connection, query));
        }

        var columns = new List<(string Table, string Column, string Type)>();
        await using (var command = new SqlCommand("""
            SELECT QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name), QUOTENAME(c.name), TYPE_NAME(c.system_type_id)
            FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var table in columns.GroupBy(c => c.Table))
        {
            var compared = table.Where(c => !NotCompared.Contains(c.Type)).Select(c => c.Column).ToList();
            var checksum = compared.Count == 0 ? "0" : $"CHECKSUM_AGG(BINARY_CHECKSUM({string.Join(", ", compared)}))";
            lines.AddRange(await LinesAsync(connection, $"SELECT 'rows', '{table.Key}', COUNT_BIG(*), {checksum} FROM {table.Key}"));
        }

        return [.. lines.Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> LinesAsync(SqlConnection connection, string sql)
    {
        var lines = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i)))));
        }

        return lines;
    }
}
