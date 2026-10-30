namespace OMTS.Application.Features.Workforce;

public sealed record SaveAttendanceSettings(DateOnly EffectiveFrom, TimeOnly? BreakFrom, TimeOnly? BreakTo, int RoundingMinutes);
