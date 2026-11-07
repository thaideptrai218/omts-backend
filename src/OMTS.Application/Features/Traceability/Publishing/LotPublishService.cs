using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Publishing;

/// <summary>
/// TRACE-01.2 publish one harvest lot from the Trace lot screen (SCR-TRACE-01): a Zone Manager of the lot's zone or the Cooperative Owner (PROVENANCE Publish, §I.4.4; the Accountant
/// never), for a Not published lot that is on a sales invoice, or, for the Owner only, a Withdrawn lot, which is published again (BR-TRACE-02). The lot is checked alone (BR-TRACE-01)
/// and the client asks MSG-GEN-04 before the publish. A lot that is Published stays as it is: it is published once.
/// </summary>
public sealed class LotPublishService(
    IHarvestLotRepository lots,
    IHarvestLotSalesQuery sales,
    ProvenanceReadiness readiness,
    ProvenancePublisher publisher,
    HarvestLotViews views,
    IUnitOfWork unitOfWork)
{
    // A parallel publish or end of sale changed the lot: read it again, then it answers what it finds (Published: MSG-GEN-09).
    private const int MaxAttempts = 3;

    public async Task<Result<LotPublishCheck>> CheckAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        var (block, onInvoice) = await BlockAsync(lot, cancellationToken);
        if (block == LotPublishBlock.NoRight)
        {
            return AppError.Forbidden;
        }

        var missing = (await readiness.MissingAsync([lot.Id], cancellationToken))[lot.Id];
        var republish = lot.OriginState == HarvestLotOriginState.Withdrawn;
        var message = missing.Count > 0 ? UserMessages.ProvenanceNotReady(lot.LotCode, missing, atEndOfSale: false)
            : block is null ? UserMessages.ConfirmAction(ConfirmActionOf(lot.LotCode, republish))
            : null;
        return new LotPublishCheck(
            lot.Id, lot.LotCode, lot.OriginState, lot.PublishedAt, lot.WithdrawnAt, onInvoice, block is null, block, republish, missing.Count == 0, missing, message?.Text, message?.Code);
    }

    /// <summary>403 MSG-GEN-08 without the right or for a Zone Manager on a Withdrawn lot; 409 MSG-GEN-09 for a Published lot or one on no sales invoice; 409 MSG-TRACE-01 with <c>missing</c>.</summary>
    public async Task<Result<LotPublished>> PublishAsync(Guid lotId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var lot = await lots.GetForEditAsync(lotId, cancellationToken);
            if (lot is null)
            {
                return AppError.NotFound;
            }

            var (block, _) = await BlockAsync(lot, cancellationToken);
            if (Refusal(block, lot) is { } refused)
            {
                return refused;
            }

            var missing = (await readiness.MissingAsync([lot.Id], cancellationToken))[lot.Id];
            if (missing.Count > 0)
            {
                return AppError.Conflict(
                    UserMessages.ProvenanceNotReady(lot.LotCode, missing, atEndOfSale: false), new Dictionary<string, object?> { ["lotCode"] = lot.LotCode, ["missing"] = missing });
            }

            var republished = lot.OriginState == HarvestLotOriginState.Withdrawn;
            publisher.Publish(lot, salesInvoiceCode: null);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                unitOfWork.DiscardChanges();
                continue;
            }

            var saved = UserMessages.Saved("Harvest lot");
            return new LotPublished(await views.ViewAsync(lot, lot.Raft, cancellationToken), republished, saved.Text, saved.Code);
        }
    }

    /// <summary>{action} of MSG-GEN-04 (TRACE-01.2 step 2).</summary>
    public static string ConfirmActionOf(string lotCode, bool republish) =>
        republish ? $"publish again the origin information of harvest lot {lotCode}" : $"publish the origin information of harvest lot {lotCode}";

    private async Task<(LotPublishBlock? Block, bool OnSalesInvoice)> BlockAsync(HarvestLot lot, CancellationToken cancellationToken)
    {
        var onInvoice = (await sales.LotsOnCurrentInvoiceAsync([lot.Id], cancellationToken)).Contains(lot.Id);
        return (LotPublishRules.Block(await views.AccessAsync(cancellationToken), lot, lot.Raft.ZoneId, onInvoice), onInvoice);
    }

    private static AppError? Refusal(LotPublishBlock? block, HarvestLot lot) => block switch
    {
        null => null,
        LotPublishBlock.NoRight or LotPublishBlock.OwnerOnly => AppError.Forbidden,
        LotPublishBlock.AlreadyPublished => AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", nameof(HarvestLotOriginState.Published))),
        _ => AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", "on no sales invoice")),
    };
}
