using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record ResetPasswordRequest(string VerificationToken, string NewPassword, string PasswordConfirmation);
