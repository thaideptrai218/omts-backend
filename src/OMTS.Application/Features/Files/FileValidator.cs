namespace OMTS.Application.Features.Files;

/// <summary>A checked file: its real format and the content type to store it with.</summary>
public sealed record ValidatedFile(IncomingFile File, FileFormat Format, string ContentType);

/// <summary>Result of <see cref="FileValidator.Validate"/>: the checked files, or the first invalid file's name.</summary>
public sealed record FileValidationResult(IReadOnlyList<ValidatedFile> Files, string? InvalidFileName, string? Problem)
{
    public bool IsValid => Problem is null;
}

/// <summary>
/// Checks uploads before they reach storage: the real format is read from the content (magic bytes), not
/// from the extension or the client's content type, so a renamed .exe is rejected. Callers map a failure
/// to MSG01 naming the field.
/// </summary>
public static class FileValidator
{
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] RiffSignature = "RIFF"u8.ToArray();
    private static readonly byte[] WebPSignature = "WEBP"u8.ToArray();

    public static FileValidationResult Validate(IReadOnlyList<IncomingFile> files, FileRules rules)
    {
        if (files.Count > rules.MaxFiles)
        {
            return Invalid(null, $"At most {rules.MaxFiles} files can be saved at once.");
        }

        var validated = new List<ValidatedFile>(files.Count);
        foreach (var file in files)
        {
            if (file.Length <= 0)
            {
                return Invalid(file.FileName, "The file is empty.");
            }

            if (file.Length > rules.MaxBytesPerFile)
            {
                return Invalid(file.FileName, $"The file is larger than {rules.MaxBytesPerFile / (1024 * 1024)} MB.");
            }

            var format = DetectFormat(file.Content);
            if (format is null || !rules.AllowedFormats.Contains(format.Value))
            {
                return Invalid(file.FileName, $"Allowed formats: {string.Join(", ", rules.AllowedFormats)}.");
            }

            validated.Add(new ValidatedFile(file, format.Value, ContentTypeOf(format.Value)));
        }

        return new FileValidationResult(validated, null, null);
    }

    /// <summary>Reads the first bytes and restores the stream position.</summary>
    public static FileFormat? DetectFormat(Stream content)
    {
        if (!content.CanSeek)
        {
            throw new ArgumentException("The upload stream must be seekable.", nameof(content));
        }

        var start = content.Position;
        Span<byte> header = stackalloc byte[12];
        var read = content.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        content.Position = start;
        return DetectFormat(header[..read]);
    }

    public static FileFormat? DetectFormat(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(JpegSignature))
        {
            return FileFormat.Jpeg;
        }

        if (header.StartsWith(PngSignature))
        {
            return FileFormat.Png;
        }

        if (header.StartsWith(PdfSignature))
        {
            return FileFormat.Pdf;
        }

        // RIFF <size> WEBP
        if (header.Length >= 12 && header.StartsWith(RiffSignature) && header[8..12].SequenceEqual(WebPSignature))
        {
            return FileFormat.WebP;
        }

        return null;
    }

    public static string ContentTypeOf(FileFormat format) => format switch
    {
        FileFormat.Jpeg => "image/jpeg",
        FileFormat.Png => "image/png",
        FileFormat.Pdf => "application/pdf",
        FileFormat.WebP => "image/webp",
        _ => "application/octet-stream",
    };

    private static FileValidationResult Invalid(string? fileName, string problem) => new([], fileName, problem);
}
