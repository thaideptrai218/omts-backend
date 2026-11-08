namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record AttendanceDayView(DateOnly Date, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt, IReadOnlyList<AttendancePeriodView> Periods, IReadOnlyList<JobHoursView> Jobs, bool Complete);
