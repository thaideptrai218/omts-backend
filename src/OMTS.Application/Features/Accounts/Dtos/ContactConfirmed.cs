using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="OldDocumentsHidden">Trader only: several offline profiles share the number (MSG57).</param>
public sealed record ContactConfirmed(string Contact, bool OldDocumentsHidden, string? Notice, string? NoticeCode);
