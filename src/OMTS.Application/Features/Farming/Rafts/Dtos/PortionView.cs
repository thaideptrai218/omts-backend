using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

public sealed record PortionView(decimal From, decimal To, PortionCondition Condition, IReadOnlyList<SegmentSourceView> Segments);
