using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

public sealed record RaftListItem(
    Guid Id, Guid ZoneId, string Code, decimal? Latitude, decimal? Longitude, string? Size, int LaneCount, int StatusId, string Status);
