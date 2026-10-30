using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Notifications.Dtos;
using OMTS.Application.Features.Notifications;

namespace OMTS.Api.Controllers.Notifications;

/// <summary>The caller's in-app notifications (web and Zalo Mini App).</summary>
[ApiController]
[Route("api/me/notifications")]
[Authorize]
public sealed class MeNotificationsController(NotificationInboxService inbox, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Newest first, with the unread count for the badge.</summary>
    [HttpGet]
    [ProducesResponseType<NotificationPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        (await inbox.ListAsync(currentUser.AccountId!.Value, page, pageSize, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken) =>
        (await inbox.MarkReadAsync(currentUser.AccountId!.Value, id, cancellationToken)).ToActionResult(_ => NoContent());

    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await inbox.MarkAllReadAsync(currentUser.AccountId!.Value, cancellationToken);
        return NoContent();
    }
}
