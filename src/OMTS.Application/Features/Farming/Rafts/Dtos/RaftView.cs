using System.Text.Json;
using OMTS.Application.Features.Employees.Dtos;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

/// <param name="MaterialCost">Total of the purchase receipts linked to the raft (INP-03); null until purchase receipts exist, never 0 for unknown.</param>
public sealed record RaftView(
    Guid Id,
    ZoneRef Zone,
    string Code,
    decimal? Latitude,
    decimal? Longitude,
    string? Location,
    string? Size,
    string? ConditionNotes,
    string? PhotoUrl,
    int StatusId,
    string Status,
    int LayoutVersion,
    IReadOnlyList<LaneView> Lanes,
    JsonElement? LayoutDrawing,
    IReadOnlyList<LayoutVersionView> LayoutVersions,
    decimal? MaterialCost,
    string RowVersion);
