using OMTS.Application.Features.Files.Models;
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
}
