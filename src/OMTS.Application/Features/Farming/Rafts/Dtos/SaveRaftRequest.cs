using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

/// <summary>
/// FARM-04 create/edit body. <see cref="LayoutDrawing"/> is the web layout editor's JSON (≤ 50 KB), stored
/// as-is; <see cref="RowVersion"/> is required on edit only.
/// </summary>
public sealed record SaveRaftRequest(
    string? Code,
    decimal? Latitude,
    decimal? Longitude,
    string? Location,
    string? Size,
    string? ConditionNotes,
    IReadOnlyList<LaneInput>? Lanes,
    JsonElement? LayoutDrawing = null,
    string? RowVersion = null);
