namespace OMTS.Application.Features.Sales.Requests.Dtos;

public sealed record StaffingEmployeeOption(Guid Id, string Name, bool Available, string? UnavailableReason);
