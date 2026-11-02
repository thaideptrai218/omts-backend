
namespace OMTS.Infrastructure.Storage;

public sealed class AzureBlobStorageOptions
{
    /// <summary>Account connection string with a shared key (needed to sign SAS URLs); "UseDevelopmentStorage=true" for Azurite.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Private container; created on first upload when missing.</summary>
    public string Container { get; set; } = "omts-files";
}
