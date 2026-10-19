namespace OMTS.Application.Features.Authorization.Dtos;

public sealed record FunctionAccess(string Function, string Name, bool AllZones, IReadOnlyCollection<Guid> ZoneIds, IReadOnlyList<string> Actions);
