using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

/// <summary>A recorded segment behind a portion of the raft map; no kg is inferred from it.</summary>
public sealed record SegmentSourceView(
    Guid Id,
    LaneSegmentType Type,
    decimal From,
    decimal To,
    DateOnly Date,
    string? RecordedBy,
    string? ConfirmedBy,
    Guid? SeedingEventId,
    string? SeedLotCode,
    Guid? HarvestEventId,
    Guid? LotId,
    string? WriteOffReason);
