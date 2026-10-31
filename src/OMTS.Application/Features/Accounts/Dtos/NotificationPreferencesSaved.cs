using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="Notice">MSG22 when push was asked for but no device allows notifications.</param>
public sealed record NotificationPreferencesSaved(
    IReadOnlyList<CategoryPreferenceView> Categories, string Message, string MessageCode, string? Notice, string? NoticeCode);
