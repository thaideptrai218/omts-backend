namespace OMTS.Application.Features.Scans;

/// <summary>What a reader got out of a photo: the raw text fields the model copied, or the <c>OcrErrorCodes</c> code it failed with.</summary>
public sealed record ScanRead<T>(T? Value, string? ErrorCode) where T : class
{
    public static ScanRead<T> Read(T value) => new(value, null);

    public static ScanRead<T> Failed(string errorCode) => new(null, errorCode);
}
