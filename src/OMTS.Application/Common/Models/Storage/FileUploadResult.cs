using OMTS.Application.Features.Files;

namespace OMTS.Application.Common.Models.Storage;

public sealed record FileUploadResult(bool Succeeded, string? Key, string? ErrorCode)
{
    public static FileUploadResult Stored(string key) => new(true, key, null);

    public static FileUploadResult Failed(string errorCode) => new(false, null, errorCode);
}
