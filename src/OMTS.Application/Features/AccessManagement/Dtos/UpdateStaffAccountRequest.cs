using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record UpdateStaffAccountRequest(string DisplayName, string Username, string PhoneNumber, string? Email, string? RowVersion);
