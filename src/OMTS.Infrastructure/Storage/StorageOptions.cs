namespace OMTS.Infrastructure.Storage;

/// <summary>"Storage" section. Connection strings and signing keys come from secrets only.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public const string LocalProvider = "Local";
    public const string AzureBlobProvider = "AzureBlob";

    public string Provider { get; set; } = LocalProvider;
    public LocalStorageOptions Local { get; set; } = new();
    public AzureBlobStorageOptions AzureBlob { get; set; } = new();
}

public sealed class LocalStorageOptions
{
    /// <summary>Folder for stored files; relative paths are under the API content root.</summary>
    public string RootPath { get; set; } = "App_Data/files";

    /// <summary>
    /// Key that signs the expiring download URLs. When empty a random key is used, so URLs stop working
    /// after a restart (fine for development).
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;
}

public sealed class AzureBlobStorageOptions
{
    /// <summary>Account connection string with a shared key (needed to sign SAS URLs); "UseDevelopmentStorage=true" for Azurite.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Private container; created on first upload when missing.</summary>
    public string Container { get; set; } = "omts-files";
}
