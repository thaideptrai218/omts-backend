using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="OldDocumentsHidden">Several offline profiles share the number (MSG57); the Owner links them later.</param>
public sealed record TraderRegistered(Guid AccountId, string Username, bool OldDocumentsHidden, string? Notice, string? NoticeCode);
