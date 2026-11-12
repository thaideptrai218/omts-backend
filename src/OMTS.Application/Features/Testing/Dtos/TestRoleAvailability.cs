namespace OMTS.Application.Features.Testing.Dtos;

public sealed record TestRoleAvailability(string CurrentRole, IReadOnlyList<string> Roles);
