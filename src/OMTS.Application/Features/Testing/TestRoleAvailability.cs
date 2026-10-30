namespace OMTS.Application.Features.Testing;

public sealed record TestRoleAvailability(string CurrentRole, IReadOnlyList<string> Roles);
