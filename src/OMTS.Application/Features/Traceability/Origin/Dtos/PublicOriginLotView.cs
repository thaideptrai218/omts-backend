using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Origin.Dtos;

/// <summary>A lot on the public page: Published with its origin, or Withdrawn with only its withdrawal time, the reason staying internal (BR-TRACE-06).</summary>
/// <param name="Detail">Null for a Withdrawn lot.</param>
public sealed record PublicOriginLotView(string LotCode, HarvestLotOriginState OriginState, DateTimeOffset? WithdrawnAt, PublicOriginLotDetail? Detail);
