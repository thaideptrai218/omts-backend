using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

public sealed record EmployeeListItem(Guid Id, string FullName, string? PhoneNumber, IReadOnlyList<ZoneRef> WorkingZones, LinkedAccountView? Account, string Status);
