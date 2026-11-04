using OMTS.Application.Features.Files;

namespace OMTS.Api.Common;

internal static class FormFileExtensions
{
    /// <summary>The upload as the application's file type, with a seekable stream for format detection.</summary>
    public static async Task<IncomingFile> ToIncomingFileAsync(this IFormFile file, CancellationToken cancellationToken)
    {
        var stream = file.OpenReadStream();
        if (!stream.CanSeek)
        {
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            stream = buffer;
        }

        return new IncomingFile(file.FileName, file.Length, stream);
    }

    /// <summary>The uploads of a form field; none when the field was left out.</summary>
    public static async Task<IReadOnlyList<IncomingFile>> ToIncomingFilesAsync(this IEnumerable<IFormFile>? files, CancellationToken cancellationToken)
    {
        var incoming = new List<IncomingFile>();
        foreach (var file in files ?? [])
        {
            incoming.Add(await file.ToIncomingFileAsync(cancellationToken));
        }

        return incoming;
    }

    /// <summary>A file came under another part name: model binding would drop it without a word, so the service refuses it after its access check (GFR-02).</summary>
    public static bool HasPartOtherThan(this IFormFileCollection files, string partName) =>
        files.Any(f => !f.Name.Equals(partName, StringComparison.OrdinalIgnoreCase));
}
