namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record SaveAttendanceSettings(DateOnly EffectiveFrom, TimeOnly? BreakFrom, TimeOnly? BreakTo, int RoundingMinutes);
