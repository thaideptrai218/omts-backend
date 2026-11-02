using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record ConfirmContactRequest(string VerificationToken);
