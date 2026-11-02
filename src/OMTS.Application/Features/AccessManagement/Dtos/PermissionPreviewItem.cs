using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <summary>What the draft rows would allow: per function, the actions and where (All zones or zone ids).</summary>
public sealed record PermissionPreviewItem(string Function, string Name, IReadOnlyList<string> Actions, bool AllZones, IReadOnlyList<Guid> ZoneIds);
