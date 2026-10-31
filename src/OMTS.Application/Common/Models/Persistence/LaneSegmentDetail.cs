using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Models.Persistence;

/// <summary>A recorded lane segment with the names and codes the raft condition view shows.</summary>
public sealed record LaneSegmentDetail(
    Guid Id,
    Guid LaneId,
    LaneSegmentType Type,
    decimal From,
    decimal To,
    DateOnly Date,
    string? RecordedBy,
    string? ConfirmedBy,
    Guid? SeedingEventId,
    string? SeedLotCode,
    string? WriteOffReason);
