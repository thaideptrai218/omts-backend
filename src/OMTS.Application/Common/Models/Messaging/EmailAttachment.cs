using OMTS.Application.Common.Models;

namespace OMTS.Application.Common.Models.Messaging;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);
