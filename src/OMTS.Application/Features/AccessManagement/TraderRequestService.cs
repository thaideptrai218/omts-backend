using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement;

/// <summary>
/// The Cooperative Owner reviews the trader accounts requested from the Zalo Mini App (web and Mini App):
/// approving opens the account like an ACS-01 registration (customer profile linked, BR-34); refusing needs a
/// reason, which the trader sees when opening the Mini App.
/// </summary>
public sealed class TraderRequestService(
    IAccountRepository accounts,
    ICooperativeRepository cooperatives,
    TraderCustomerLinker customerLinker,
    INotificationDispatcher notifications,
    IStatusCatalog statusCatalog,
    StatusChangeService statusChanges,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public const int MaxReasonLength = 1000;

    /// <summary>Requests waiting for a decision (default), or the refused ones.</summary>
    public async Task<IReadOnlyList<TraderRequestView>> ListAsync(bool rejected, CancellationToken cancellationToken)
    {
        var statusId = rejected ? StatusIds.Account.Rejected : StatusIds.Account.PendingApproval;
        return (await accounts.ListTraderRequestsAsync(statusId, cancellationToken)).Select(ToView).ToList();
    }

    public async Task<Result<TraderRequestView>> ApproveAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var trader = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (trader is not { SubjectType: SubjectType.Trader })
        {
            return AppError.NotFound;
        }

        if (trader.StatusId != StatusIds.Account.PendingApproval)
        {
            return statusChanges.WrongStatus(nameof(Account), trader);
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");

        trader.StatusId = StatusIds.Account.Active;
        await customerLinker.LinkNewTraderAsync(trader, cooperative.Id, cancellationToken);
        audit.RecordStatusChange(nameof(Account), trader.Id, trader.Username, StatusIds.Account.PendingApproval, StatusIds.Account.Active);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Sent after the save: only active accounts receive notifications.
        await notifications.NotifyAsync(
            [trader.Id],
            NotificationTypes.TraderAccessApproved,
            "Trader account approved",
            "The cooperative approved your trader account. Open OMTS from Zalo to start.",
            nameof(Account),
            trader.Username,
            isMandatory: false,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ToView(trader);
    }

    public async Task<Result<TraderRequestView>> RejectAsync(Guid accountId, ReasonRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
        {
            return AppError.Invalid("Reason");
        }

        var trader = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (trader is not { SubjectType: SubjectType.Trader })
        {
            return AppError.NotFound;
        }

        if (trader.StatusId != StatusIds.Account.PendingApproval)
        {
            return statusChanges.WrongStatus(nameof(Account), trader);
        }

        trader.StatusId = StatusIds.Account.Rejected;
        trader.RejectionReason = reason;
        audit.RecordStatusChange(nameof(Account), trader.Id, trader.Username, StatusIds.Account.PendingApproval, StatusIds.Account.Rejected, reason);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToView(trader);
    }

    private TraderRequestView ToView(Account trader) => new(
        trader.Id,
        trader.DisplayName,
        trader.PhoneNumber,
        trader.AccessRequestNote,
        trader.AccessRequestedAt,
        statusCatalog.CodeOf(trader.StatusId),
        trader.RejectionReason);
}
