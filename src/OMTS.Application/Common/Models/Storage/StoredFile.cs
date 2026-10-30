using OMTS.Application.Features.Files;

namespace OMTS.Application.Common.Models.Storage;

/// <param name="LastModified">When the file was written (UTC); a file is never rewritten after its upload.</param>
public sealed record StoredFile(string Key, DateTimeOffset LastModified);
