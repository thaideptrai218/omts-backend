namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A test that needs a real SQL Server (migrations, constraints, transactions). Runs only when
/// <see cref="VariableName"/> holds a server connection string – CI starts a container for it – and is
/// skipped otherwise, so <c>dotnet test</c> still works on a machine without SQL Server.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string VariableName = "OMTS_TEST_SQLSERVER";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName)))
        {
            Skip = $"Set {VariableName} to a SQL Server connection string to run database tests.";
        }
    }
}
