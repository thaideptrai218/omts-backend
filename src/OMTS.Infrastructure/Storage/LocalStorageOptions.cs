
namespace OMTS.Infrastructure.Storage;

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
