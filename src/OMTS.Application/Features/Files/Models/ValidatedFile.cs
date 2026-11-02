
namespace OMTS.Application.Features.Files.Models;

/// <summary>A checked file: its real format and the content type to store it with.</summary>
public sealed record ValidatedFile(IncomingFile File, FileFormat Format, string ContentType);
