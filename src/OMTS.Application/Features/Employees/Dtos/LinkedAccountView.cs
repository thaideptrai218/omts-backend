using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

public sealed record LinkedAccountView(Guid Id, string Username, string DisplayName, string Status);
