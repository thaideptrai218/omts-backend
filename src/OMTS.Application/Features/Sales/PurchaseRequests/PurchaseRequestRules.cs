using OMTS.Application.Common.Exceptions;
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

    // PM default: the SRS gives no limit for the note and the reason.
    public const int MaxTextLength = 1000;

    // decimal(18,3).
    private const decimal MaxKg = 999_999_999_999_999.999m;

    /// <summary>Above 0 and at most 3 decimals: more would be rounded by the column, possibly to 0.</summary>
    public static bool IsKg(decimal? kg) => kg is > 0 and <= MaxKg && decimal.Round(kg.Value, 3) == kg.Value;

    /// <summary>The text without its ends; null when it shows nothing, so a note of invisible characters is no note.</summary>
    public static string? CleanText(string? text) => DisplayNames.Normalize(text).Length == 0 ? null : text!.Trim();

    public static AppError WrongStatus(string status) => AppError.Conflict(UserMessages.WrongStatus(ObjectName, status));

    /// <summary>
    /// The save of a status change. The loser of a race gets the status that won when it forbids the action (MSG-GEN-09,
    /// SALE-02.0.E1, SALE-03.0.E1), otherwise the conflict goes on to the handler as 409 MSG06, read again. Null when saved.
    /// </summary>
    /// <param name="statusName">The display name of a status.</param>
    public static async Task<AppError?> SaveAsync(
        IUnitOfWork unitOfWork, PurchaseRequest request, Func<int, bool> allowsAction, Func<int, string> statusName, CancellationToken cancellationToken)
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

            return WrongStatus(statusName(request.StatusId));
        }
    }

    /// <summary>SALE-02 step 1, before the Fulfilled check.</summary>
    public static bool AllowsCancel(int statusId) => statusId is StatusIds.PurchaseRequest.AwaitingResponse or StatusIds.PurchaseRequest.Accepted;

    /// <summary>SALE-03 step 1: a response cannot be changed once it is sent (BR-SALE-02).</summary>
    public static bool AllowsResponse(int statusId) => statusId == StatusIds.PurchaseRequest.AwaitingResponse;
}
