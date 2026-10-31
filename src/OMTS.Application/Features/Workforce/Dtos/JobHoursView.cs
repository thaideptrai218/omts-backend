namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record JobHoursView(Guid JobTypeId, string Job, decimal Hours);
