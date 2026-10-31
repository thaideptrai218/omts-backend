using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

public sealed record LayoutVersionView(int VersionNumber, int LaneCount, DateOnly ValidFrom, DateOnly? ValidTo);
