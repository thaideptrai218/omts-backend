namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record AttendancePeriodView(Guid JobTypeId, string Job, string? Raft, DateTimeOffset StartsAt, DateTimeOffset EndsAt, decimal Minutes);
