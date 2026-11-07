using System.Net;
using Microsoft.Data.SqlClient;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// NFR-09 and the project's rule that a race answers 2xx, 404 or 409: a deadlock lost outside a save, as a read on a database with locking reads, reaches the exception handler bare. These run the
/// handler on real SQL Server failures, without the timing of the parallel test (<c>L2_everything_on_one_cycle_in_parallel_never_answers_500_with_locking_reads_too</c>).
/// </summary>
public class ProblemExceptionHandlerDeadlockTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const int DeadlockVictim = 1205;
    private const int DivideByZero = 8134;

    [SqlServerFact]
    public async Task A_deadlock_victim_answers_the_edit_conflict_bare_or_wrapped()
    {
        var victim = await DeadlockVictimAsync();

        await (await SendAsync(victim)).SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        await (await SendAsync(new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", victim))).SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
    }

    // Only 1205 is forgiven: any other failure of the database stays a server error, bare or wrapped, and tells the client nothing of it.
    [SqlServerFact]
    public async Task Another_database_failure_stays_a_server_error()
    {
        var other = await DivideByZeroAsync();

        var bare = await (await SendAsync(other)).SrsProblemAsync(HttpStatusCode.InternalServerError, "UNEXPECTED_ERROR", null);
        await (await SendAsync(new InvalidOperationException("Wrapped.", other))).SrsProblemAsync(HttpStatusCode.InternalServerError, "UNEXPECTED_ERROR", null);

        Assert.DoesNotContain("divide", bare.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private Task<HttpResponseMessage> SendAsync(Exception failure)
    {
        var id = Guid.NewGuid();
        TestOnlyMessageQaController.Failures[id] = failure;
        return factory.CreateClient().GetAsync($"/test-only/qa-messages/failure/{id}");
    }

    private static async Task<SqlException> DivideByZeroAsync()
    {
        await using var connection = await OpenAsync();
        var failure = await Assert.ThrowsAsync<SqlException>(() => RunAsync(connection, "SELECT 1 / 0"));
        Assert.Equal(DivideByZero, failure.Number);
        return failure;
    }

    // Two transactions update the same two rows in opposite order, and the one with the low priority is rolled back.
    private static async Task<SqlException> DeadlockVictimAsync()
    {
        var table = $"##deadlock_{Guid.NewGuid():N}";
        await using var victim = await OpenAsync();
        await using var other = await OpenAsync();
        await RunAsync(other, $"CREATE TABLE {table} (Id int PRIMARY KEY, Value int NOT NULL); INSERT INTO {table} VALUES (1, 0), (2, 0)");
        await RunAsync(victim, $"SET DEADLOCK_PRIORITY LOW; BEGIN TRANSACTION; UPDATE {table} SET Value = 1 WHERE Id = 1");
        await RunAsync(other, $"BEGIN TRANSACTION; UPDATE {table} SET Value = 1 WHERE Id = 2");

        var waiting = RunAsync(victim, $"UPDATE {table} SET Value = 2 WHERE Id = 2");
        await Task.Delay(TimeSpan.FromSeconds(1));
        await RunAsync(other, $"UPDATE {table} SET Value = 2 WHERE Id = 1");

        var failure = await Assert.ThrowsAsync<SqlException>(() => waiting);
        Assert.Equal(DeadlockVictim, failure.Number);
        return failure;
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(Environment.GetEnvironmentVariable(SqlServerFactAttribute.VariableName));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task RunAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
