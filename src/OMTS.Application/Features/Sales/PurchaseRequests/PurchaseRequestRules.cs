using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>What the three use cases share: the entries they read (BR-SALE-01, BR-SALE-02) and how a lost race answers.</summary>
internal static class PurchaseRequestRules
{
    public const string RecordType = nameof(PurchaseRequest);
    public const string ObjectName = "Purchase request";
    public const string FulfilledStatus = "Fulfilled";

    /// <summary>The harvest is marked done (decision of 11/11/2026): no lot is added any more.</summary>
    public const string HarvestDoneStatus = "marked harvest done";

    /// <summary>The harvest is not marked done yet: the request is not weighed before it.</summary>
    public const string HarvestNotDoneStatus = "not marked harvest done";

    /// <summary>No lot was recorded for the request: its harvest cannot be done.</summary>
    public const string NoHarvestLotStatus = "without a harvest lot";

    /// <summary>The request has a weighing: its harvest stays done.</summary>
    public const string WeighedStatus = "weighed";

    // PM default: the SRS gives no limit for the note and the reason.
    public const int MaxTextLength = 1000;

    /// <summary>Whole cages above 0 (decision of 08/11/2026: requests are in cages, the kg of each cage is weighed at the port).</summary>
    public static bool IsCages(int? cages) => cages is > 0;

    /// <summary>The code the screens, the harvest and the port name a request by: the day it is sent and the start of its id.</summary>
    public static string CodeOf(PurchaseRequest request, DateOnly sentOn) => $"YC-{sentOn:yyyyMMdd}-{request.Id.ToString("N")[..12].ToUpperInvariant()}";

    /// <summary>The text without its ends; null when it shows nothing, so a note of invisible characters is no note.</summary>
    public static string? CleanText(string? text) => DisplayNames.Normalize(text).Length == 0 ? null : text!.Trim();

    public static AppError WrongStatus(string status) => AppError.Conflict(UserMessages.WrongStatus(ObjectName, status));

    /// <summary>
    /// The save of a status change. The loser of a race gets the status that won when it forbids the action (MSG-GEN-09,
    /// SALE-02.0.E1, SALE-03.0.E1), otherwise the conflict goes on to the handler as 409 MSG06, read again. Null when saved.
    /// </summary>
    /// <param name="statusName">The name to show of the status that won.</param>
    public static async Task<AppError?> SaveAsync(
        IUnitOfWork unitOfWork, PurchaseRequest request, Func<int, bool> allowsAction, Func<PurchaseRequest, Task<string>> statusName, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.ReloadAsync(request, cancellationToken);
            if (allowsAction(request.StatusId))
            {
                throw;
            }

            return WrongStatus(await statusName(request));
        }
    }

    /// <summary>
    /// What a harvest lot is recorded for (decision of 11/11/2026): an Accepted request whose harvest is not marked done, and not Fulfilled, which a request taken in full
    /// before that decision can be. Null when the lot may be recorded for it.
    /// </summary>
    public static async Task<AppError?> RefusesHarvestLotAsync(
        PurchaseRequest? request, IStatusCatalog statuses, IPurchaseRequestFulfilment fulfilment, CancellationToken cancellationToken) => request switch
    {
        null => AppError.Invalid(ObjectName),
        { StatusId: not StatusIds.PurchaseRequest.Accepted } => WrongStatus(statuses.DisplayNameOf(request.StatusId)),
        { HarvestCompletedAt: not null } => WrongStatus(HarvestDoneStatus),
        _ => (await fulfilment.FulfilledAmongAsync([request.Id], cancellationToken)).Contains(request.Id) ? WrongStatus(FulfilledStatus) : null,
    };

    /// <summary>SALE-02 step 1, before the Fulfilled check. A request whose staff are being asked is not answered yet.</summary>
    public static bool AllowsCancel(int statusId) =>
        statusId is StatusIds.PurchaseRequest.AwaitingResponse or StatusIds.PurchaseRequest.AwaitingStaff or StatusIds.PurchaseRequest.Accepted;

    /// <summary>
    /// SALE-03 step 1: a response cannot be changed once it is sent (BR-SALE-02). Awaiting staff is still before the response: the Owner
    /// accepts once the staff agreed (decision of 31/10/2026), or declines.
    /// </summary>
    public static bool AllowsResponse(int statusId) => statusId is StatusIds.PurchaseRequest.AwaitingResponse or StatusIds.PurchaseRequest.AwaitingStaff;
}
