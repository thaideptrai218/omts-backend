using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <summary>Returned once, never stored in clear, never sent by SMS or email (ACS-09).</summary>
public sealed record StaffAccountWithPassword(StaffAccountView Account, string TemporaryPassword, DateTimeOffset TemporaryPasswordExpiresAt);
