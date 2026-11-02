using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Verification;

/// <summary>"Verification" section: system-wide limits on code delivery.</summary>
public sealed class VerificationLimitOptions
{
    public const string SectionName = "Verification";

    /// <summary>
    /// SMS codes (first sends and resends) the whole system may send in 24 hours. Beyond it SMS codes answer
    /// MSG04 and a critical log entry alerts the operator (cost control against SMS pumping).
    /// </summary>
    public int DailySmsLimit { get; set; } = 500;
}
