
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
