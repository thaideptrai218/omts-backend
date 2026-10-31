namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record AttendanceQrView(string Code, DateTimeOffset ExpiresAt);
