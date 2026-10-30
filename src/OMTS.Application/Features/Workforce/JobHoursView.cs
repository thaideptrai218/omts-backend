namespace OMTS.Application.Features.Workforce;

public sealed record JobHoursView(Guid JobTypeId, string Job, decimal Hours);
