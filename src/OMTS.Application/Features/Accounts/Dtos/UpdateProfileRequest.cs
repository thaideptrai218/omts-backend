using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record UpdateProfileRequest(string DisplayName, string? RowVersion, string? Username = null);
