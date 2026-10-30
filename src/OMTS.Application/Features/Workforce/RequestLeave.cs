namespace OMTS.Application.Features.Workforce;

public sealed record RequestLeave(DateOnly From, DateOnly To, string Reason, Guid ClientOperationId);
