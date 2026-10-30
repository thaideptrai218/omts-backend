using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Models;

/// <summary>A recorded lane segment, in metres from marker A.</summary>
public sealed record SegmentFact(Guid Id, LaneSegmentType Type, decimal From, decimal To, DateOnly Date);
