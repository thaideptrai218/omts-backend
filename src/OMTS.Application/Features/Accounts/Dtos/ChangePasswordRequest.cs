using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="CurrentPassword">Proof by the current password…</param>
/// <param name="VerificationToken">…or by an email code with purpose ChangePassword.</param>
public sealed record ChangePasswordRequest(
    string? CurrentPassword,
    string? VerificationToken,
    string NewPassword,
    string PasswordConfirmation,
    ClientType ClientType = ClientType.Web,
    string? DeviceName = null);
