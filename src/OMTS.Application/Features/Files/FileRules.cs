namespace OMTS.Application.Features.Files;

public enum FileFormat
{
    Jpeg,
    Png,
    Pdf,
    WebP,
}

/// <summary>Which formats, how large and how many files one save may carry.</summary>
public sealed record FileRules(IReadOnlySet<FileFormat> AllowedFormats, long MaxBytesPerFile, int MaxFiles)
{
    public const long TenMegabytes = 10L * 1024 * 1024;

    /// <summary>Photos (raft, meter, evidence): JPEG or PNG, stored unchanged since they may be OCR evidence.</summary>
    public static readonly FileRules Photos = new(new HashSet<FileFormat> { FileFormat.Jpeg, FileFormat.Png }, TenMegabytes, 10);

    /// <summary>Cooperative logo (FARM-01): PNG, JPEG or WebP, at most 2 MB.</summary>
    public static readonly FileRules Logo =
        new(new HashSet<FileFormat> { FileFormat.Png, FileFormat.Jpeg, FileFormat.WebP }, 2L * 1024 * 1024, 1);

    /// <summary>
    /// Cooperative cover photo (FARM-01): PNG, JPEG or WebP, at most 5 MB. A wide banner, larger than the logo, still
    /// light enough for the public page on a phone.
    /// </summary>
    public static readonly FileRules CoverPhoto =
        new(new HashSet<FileFormat> { FileFormat.Png, FileFormat.Jpeg, FileFormat.WebP }, 5L * 1024 * 1024, 1);

    /// <summary>Documents (licenses, invoices, certificates): JPEG, PNG or PDF.</summary>
    public static readonly FileRules Documents =
        new(new HashSet<FileFormat> { FileFormat.Jpeg, FileFormat.Png, FileFormat.Pdf }, TenMegabytes, 10);
}
