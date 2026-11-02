using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

public sealed record LaneConditionView(
    int LaneNumber, decimal Length, LaneSummary Summary, IReadOnlyList<PortionView> Portions, IReadOnlyList<SegmentSourceView> PlannedHarvests);
