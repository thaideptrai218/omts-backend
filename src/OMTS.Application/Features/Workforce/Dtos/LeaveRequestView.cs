namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record LeaveRequestView(Guid Id, string EmployeeName, DateOnly From, DateOnly To, string Reason, bool? Approved, string? Response, string RowVersion, DateTimeOffset? CancelledAt);
