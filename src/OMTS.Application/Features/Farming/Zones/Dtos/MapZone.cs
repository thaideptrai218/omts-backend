using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones.Dtos;

public sealed record MapZone(Guid Id, string Code, string Name, decimal? Latitude, decimal? Longitude, int StatusId, string Status, IReadOnlyList<MapRaft> Rafts);
