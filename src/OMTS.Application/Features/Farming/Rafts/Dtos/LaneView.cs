using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

public sealed record LaneView(Guid Id, int LaneNumber, decimal? Length, string? MarkerA, string? MarkerB);
