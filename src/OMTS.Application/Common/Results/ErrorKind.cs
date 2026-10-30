using OMTS.Application.Common.Messages;

namespace OMTS.Application.Common.Results;

/// <summary>What kind of failure a use case reports; the API maps it to an HTTP status.</summary>
public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Locked,
    TooManyRequests,
    ServiceUnavailable,
    BadGateway,

    /// <summary>The server could not complete the operation (e.g. generating an export file).</summary>
    Internal,
}
