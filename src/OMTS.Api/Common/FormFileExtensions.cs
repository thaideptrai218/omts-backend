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

    /// <summary>Every upload of a multipart request (e.g. several evidence photos under one field name).</summary>
    public static async Task<IReadOnlyList<IncomingFile>> ToIncomingFilesAsync(this IEnumerable<IFormFile>? files, CancellationToken cancellationToken)
    {
        var result = new List<IncomingFile>();
        foreach (var file in files ?? [])
        {
            result.Add(await file.ToIncomingFileAsync(cancellationToken));
        }

        return result;
    }
}
