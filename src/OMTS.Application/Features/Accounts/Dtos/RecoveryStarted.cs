using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <summary>Always the same answer (MSG15), whether or not a code was sent.</summary>
public sealed record RecoveryStarted(Guid RequestId, string Message, string MessageCode);
