namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record RequestLeave(DateOnly From, DateOnly To, string Reason, Guid ClientOperationId);
