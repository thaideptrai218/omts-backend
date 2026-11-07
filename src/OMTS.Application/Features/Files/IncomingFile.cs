namespace OMTS.Application.Features.Files;

/// <summary>A file sent by a client, independent of ASP.NET's IFormFile. The stream must be seekable.</summary>
public sealed record IncomingFile(string FileName, long Length, Stream Content);
