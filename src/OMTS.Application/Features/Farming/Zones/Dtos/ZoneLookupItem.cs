using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones.Dtos;

/// <summary>Picker row for other modules (water, harvest, sales…).</summary>
public sealed record ZoneLookupItem(Guid Id, string Code, string Name, int StatusId, string Status);
