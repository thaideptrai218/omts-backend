using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

/// <param name="CycleId">The cycle the view is built from; null when the raft had no cycle on that date.</param>
public sealed record RaftConditionView(Guid RaftId, string RaftCode, DateOnly Date, Guid? CycleId, int LayoutVersion, IReadOnlyList<LaneConditionView> Lanes);
