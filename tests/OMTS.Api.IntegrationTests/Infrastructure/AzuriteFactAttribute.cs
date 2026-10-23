namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A test that needs a real Azure Blob endpoint. Runs only when <see cref="VariableName"/> holds a blob
/// connection string with an account key (CI starts Azurite and sets <c>UseDevelopmentStorage=true</c>), and
/// is skipped otherwise, like <see cref="SqlServerFactAttribute"/>.
/// </summary>
public sealed class AzuriteFactAttribute : FactAttribute
{
    public const string VariableName = "OMTS_TEST_AZURITE";

    public AzuriteFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName)))
        {
            Skip = $"Set {VariableName} to an Azure Blob connection string (e.g. UseDevelopmentStorage=true) to run blob storage tests.";
        }
    }
}
