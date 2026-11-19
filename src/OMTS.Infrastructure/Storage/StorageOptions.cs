namespace OMTS.Infrastructure.Storage;

/// <summary>"Storage" section. Connection strings and signing keys come from secrets only.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public const string LocalProvider = "Local";
    public const string AzureBlobProvider = "AzureBlob";
    public const string R2Provider = "R2";

    public string Provider { get; set; } = LocalProvider;
    public LocalStorageOptions Local { get; set; } = new();
    public AzureBlobStorageOptions AzureBlob { get; set; } = new();
    public R2StorageOptions R2 { get; set; } = new();
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

/// <summary>Cloudflare R2 (S3-compatible). The keys come from secrets (environment variables) only; the bucket is private.</summary>
public sealed class R2StorageOptions
{
    /// <summary>Cloudflare account id: the endpoint is https://{AccountId}.r2.cloudflarestorage.com.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Access key id of an R2 API token with Object Read &amp; Write on the bucket.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Private bucket, created once by the operator.</summary>
    public string Bucket { get; set; } = "omts-files";

    /// <summary>S3 endpoint; empty means the account endpoint (set it for an EU jurisdiction bucket: https://{AccountId}.eu.r2.cloudflarestorage.com).</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    public string Endpoint => string.IsNullOrWhiteSpace(ServiceUrl) ? $"https://{AccountId}.r2.cloudflarestorage.com" : ServiceUrl;
}
