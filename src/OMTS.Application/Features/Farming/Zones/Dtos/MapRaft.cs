using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones.Dtos;

public sealed record MapRaft(Guid Id, string Code, decimal? Latitude, decimal? Longitude);
