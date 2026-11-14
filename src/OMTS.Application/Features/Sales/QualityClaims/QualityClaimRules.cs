using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>What CLAIM-01 and CLAIM-02 share: the claim's rules (BR-CLAIM-01, BR-CLAIM-02) and how a lost race answers.</summary>
internal static class QualityClaimRules
{
    public const string RecordType = nameof(QualityClaim);
    public const string ObjectName = "Quality claim";

    /// <summary>The Owner responds once, while the claim is Awaiting response (BR-CLAIM-02).</summary>
    public static bool AllowsResponse(int statusId) => statusId == StatusIds.QualityClaim.AwaitingResponse;

    /// <summary>The trader answers once, while the offer of the Owner is waiting.</summary>
    public static bool AllowsAnswer(int statusId) => statusId == StatusIds.QualityClaim.OfferMade;

    public static AppError WrongStatus(string status) => AppError.Conflict(UserMessages.WrongStatus(ObjectName, status));

    public static AppError InvoiceHasAwaitingClaim(string invoiceCode) =>
        AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoiceCode}", "under a quality claim Awaiting response"));

    /// <summary>
    /// The save of a response. The loser of a race gets the status that won when it is no longer Awaiting (MSG-GEN-09), otherwise the conflict goes on to the handler as 409 MSG06.
    /// Null when saved.
    /// </summary>
    public static Task<AppError?> SaveAsync(IUnitOfWork unitOfWork, QualityClaim claim, Func<QualityClaim, string> statusName, CancellationToken cancellationToken) =>
        SaveAsync(unitOfWork, claim, statusName, AllowsResponse, cancellationToken);

    /// <summary>The same for an answer of the trader: the loser of a race gets the status that won when the offer is no longer waiting.</summary>
    public static async Task<AppError?> SaveAsync(
        IUnitOfWork unitOfWork, QualityClaim claim, Func<QualityClaim, string> statusName, Func<int, bool> stillOpen, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (ConcurrencyConflictException)
        {
            await unitOfWork.ReloadAsync(claim, cancellationToken);
            if (stillOpen(claim.StatusId))
            {
                throw;
            }

            return WrongStatus(statusName(claim));
        }
    }
}
