using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Notifications.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// ACCOUNT-08 View notifications: the caller's own notification list (any registered user). SMS and Zalo OA sendings
/// are never listed. A notification of another account answers 404 NOT_FOUND.
/// </summary>
[ApiController]
[Route("api/me/notifications")]
[Authorize]
public sealed class MeNotificationsController(NotificationInboxService inbox, ICurrentUser currentUser) : ControllerBase
{
    private Guid AccountId => currentUser.AccountId!.Value;

    /// <summary>Newest first, with <c>unreadCount</c> of the whole list; <c>unreadOnly</c> lists the unread ones only.</summary>
    [HttpGet]
    [ProducesResponseType<NotificationPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(bool unreadOnly = false, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await inbox.ListAsync(AccountId, unreadOnly, page, pageSize, cancellationToken));

    /// <summary>Marks one notification as read (its read time is kept once set) and answers it, as when it is opened.</summary>
    [HttpPost("{id:guid}/read")]
    [ProducesResponseType<NotificationView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken) =>
        (await inbox.MarkReadAsync(AccountId, id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Marks every unread notification of the list as read.</summary>
    [HttpPost("read-all")]
    [ProducesResponseType<NotificationsMarkedRead>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken) =>
        Ok(await inbox.MarkAllReadAsync(AccountId, cancellationToken));

    /// <summary>
    /// Resolves the related record and checks again that the caller may open it: 403 MSG-GEN-08 when the access was
    /// lost (e.g. a revoked zone–role pair), 404 MSG-ACCOUNT-22 when the record no longer exists (the notification
    /// stays). Changes nothing; mark the notification read with <c>POST …/{id}/read</c>.
    /// </summary>
    [HttpGet("{id:guid}/open")]
    [ProducesResponseType<OpenedNotificationRecord>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Open(Guid id, CancellationToken cancellationToken) =>
        (await inbox.OpenAsync(AccountId, id, cancellationToken)).ToActionResult(Ok);
}
