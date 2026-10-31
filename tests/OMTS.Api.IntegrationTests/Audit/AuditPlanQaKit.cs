using System.Data;
using System.Data.Common;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Repositories;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// QA helpers for the ACCOUNT-10 query plans on SQL Server: a log of <see cref="Rows"/> events over about 4.5 years, and
/// the actual plan of each query the audit search sends for a filter.
/// </summary>
internal static class AuditPlanQaKit
{
    public const int Rows = 40_000;

    private static readonly XNamespace Plan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>The start of a Vietnam day, as the search turns <c>from</c>/<c>to</c> into instants.</summary>
    public static DateTimeOffset Start(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);

    /// <summary>Runs the ACCOUNT-10 search as the API does (page 1 of 20) and returns the actual plan of each query.</summary>
    public static async Task<List<(bool IsPage, List<string> Operators, long RowsRead, string Text)>> PlansAsync(
        SqlServerTestDatabase database, AuditEventFilter filter)
    {
        var capture = new CapturingInterceptor();
        await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>()
            .UseSqlServer(database.ConnectionString).AddInterceptors(capture).Options);
        await new AuditEventQuery(db).SearchAsync(filter, 1, 20, CancellationToken.None);
        var plans = new List<(bool, List<string>, long, string)>();
        foreach (var (text, parameters) in capture.Commands)
        {
            var (operators, rowsRead) = await OperatorsAsync(database, text, parameters);
            plans.Add((text.Contains("OFFSET", StringComparison.Ordinal), operators, rowsRead, text));
        }

        return plans;
    }

    /// <summary>
    /// A migrated database with 40 accounts and <see cref="Rows"/> events, one an hour back from now: 1 in 10 by the
    /// system, 1 in 7 by one of 13 employees, 50 actions, 12 record types (and 40 events of "RareType"), 5 000 record codes
    /// of three events each and 20 zones.
    /// </summary>
    public static async Task<(Guid[] Accounts, Guid[] Employees, Guid[] Zones)> SeedAsync(SqlServerTestDatabase database)
    {
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var accounts = Enumerable.Range(0, 40).Select(_ => new Account { Username = NewCode(), DisplayName = "Staff", SubjectType = SubjectType.Staff }).ToList();
        await using (var db = database.CreateContext())
        {
            db.Accounts.AddRange(accounts);
            await db.SaveChangesAsync();
        }

        var employees = Enumerable.Range(0, 13).Select(_ => Guid.NewGuid()).ToArray();
        var zones = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var table = new DataTable();
        foreach (var (column, type) in new (string, Type)[]
        {
            ("Id", typeof(Guid)), ("ActorAccountId", typeof(Guid)), ("ActorEmployeeId", typeof(Guid)), ("Action", typeof(string)),
            ("RecordType", typeof(string)), ("RecordId", typeof(Guid)), ("RecordCode", typeof(string)), ("OccurredAt", typeof(DateTimeOffset)),
            ("ZoneId", typeof(Guid)),
        })
        {
            table.Columns.Add(column, type).AllowDBNull = true;
        }

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < Rows; i++)
        {
            table.Rows.Add(
                Guid.NewGuid(),
                i % 10 == 0 ? DBNull.Value : accounts[i % 40].Id,
                i % 7 == 0 ? employees[i % 13] : DBNull.Value,
                $"Action{i % 50:D2}.Done",
                i % 1000 == 999 ? "RareType" : $"Type{i % 12:D2}",
                Guid.NewGuid(),
                $"KV{i % 5000:D5}-B{i % 3}",
                now.AddHours(-i),
                zones[i % 20]);
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "AuditEvents" })
        {
            foreach (DataColumn column in table.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(table);
        }

        await using (var statistics = new SqlCommand("UPDATE STATISTICS AuditEvents WITH FULLSCAN", connection))
        {
            await statistics.ExecuteNonQueryAsync();
        }

        return (accounts.Select(a => a.Id).ToArray(), employees, zones);
    }

    /// <summary>
    /// The actual plan of the command as EF sent it: "physical operator index" for each operator, and the rows the
    /// operators on the audit log read.
    /// </summary>
    private static async Task<(List<string> Operators, long RowsRead)> OperatorsAsync(SqlServerTestDatabase database, string text, SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var on = new SqlCommand("SET STATISTICS XML ON", connection))
        {
            await on.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(text, connection);
        command.Parameters.AddRange(parameters.Select(p => (SqlParameter)((ICloneable)p).Clone()).ToArray());
        string? showplan = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase) && await reader.ReadAsync())
                {
                    showplan = reader.GetString(0);
                }
            }
            while (await reader.NextResultAsync());
        }

        var operators = XDocument.Parse(showplan ?? throw new InvalidOperationException("No plan returned."))
            .Descendants(Plan + "RelOp")
            .Select(op => (
                Name: $"{op.Attribute("PhysicalOp")!.Value} {op.Elements().Elements(Plan + "Object").FirstOrDefault()?.Attribute("Index")?.Value.Trim('[', ']')}".Trim(),
                Rows: op.Elements(Plan + "RunTimeInformation").Elements().Sum(t => (long?)t.Attribute("ActualRowsRead") ?? (long?)t.Attribute("ActualRows") ?? 0)))
            .ToList();
        return (operators.Select(o => o.Name).ToList(), operators.Where(o => o.Name.Contains("AuditEvents", StringComparison.Ordinal)).Sum(o => o.Rows));
    }

    /// <summary>Keeps the SQL and a copy of the parameters of every command that reads the audit log.</summary>
    private sealed class CapturingInterceptor : DbCommandInterceptor
    {
        public List<(string Text, SqlParameter[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
        {
            if (command.CommandText.Contains("[AuditEvents]", StringComparison.Ordinal))
            {
                Commands.Add((command.CommandText, command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone()).ToArray()));
            }
        }
    }
}
