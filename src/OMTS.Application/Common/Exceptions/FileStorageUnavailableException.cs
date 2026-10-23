namespace OMTS.Application.Common.Exceptions;

/// <summary>
/// The file store failed while reading (not a missing file, which is null). Thrown by the read paths of
/// <see cref="Interfaces.Storage.IFileStorage"/>; the API maps an unhandled one to 502 MSG05.
/// </summary>
public sealed class FileStorageUnavailableException(string key, Exception innerException)
    : Exception($"The file store could not read '{key}'.", innerException);
