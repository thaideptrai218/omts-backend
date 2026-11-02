using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Models;

/// <summary>A stretch of the lane with one condition and the segments that make it so.</summary>
public sealed record LanePortion(decimal From, decimal To, PortionCondition Condition, IReadOnlyList<Guid> SegmentIds);
