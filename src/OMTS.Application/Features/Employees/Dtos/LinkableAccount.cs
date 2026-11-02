using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

public sealed record LinkableAccount(Guid Id, string DisplayName, string Username, string? PhoneNumber, string Status);
