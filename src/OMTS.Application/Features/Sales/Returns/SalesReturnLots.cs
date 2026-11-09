using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>What a sales return did to its lots, for the audit row.</summary>
public sealed record SalesReturnLotChanges(IReadOnlyList<string> Reopened, IReadOnlyList<string> Closed);

/// <summary>
/// The lots of a sales return (BR-CLAIM-03, BR-HARVEST-04). Back for sale: a Closed lot is Reopened, whatever closed it. Thrown away: when the lot is still for sale, MSG-HARVEST-02 asks whether it
/// stays for sale; No closes it through HARVEST-02.1. A lot that also takes oysters back for sale on this return stays for sale, so it is not asked. Every lot joins the save (row version), so a
/// sale, a close or the daily check at the same moment makes one side fail.
/// </summary>
public sealed class SalesReturnLots(IHarvestLotRepository lots, IHarvestLotSalesQuery sales, HarvestLotStatusService statuses, HarvestLotClosingService closing, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    public const string AnswerField = "thrownAwayLots";

    /// <summary>Answers the first unanswered question or a refusal; else the lots changed, not saved yet.</summary>
    public async Task<Result<SalesReturnLotChanges>> ApplyAsync(
        IReadOnlyDictionary<Guid, SalesReturnOutcome> outcomes, SalesInvoice invoice, IReadOnlyList<ThrownAwayLotAnswer?>? answers, CancellationToken cancellationToken)
    {
        var linesByLot = invoice.Lines.ToLookup(l => l.HarvestLotId);
        var rows = await lots.ListForInvoiceAsync(linesByLot.Select(g => g.Key).ToList(), cancellationToken);
        var backForSale = linesByLot.Where(g => g.Any(l => outcomes[l.Id] == SalesReturnOutcome.BackForSale)).Select(g => g.Key).ToHashSet();

        var toAsk = rows.Where(l => !backForSale.Contains(l.Id) && l.IsForSale).ToDictionary(l => l.Id);
        var given = (answers ?? []).ToList();
        if (given.Any(a => a is null || !toAsk.ContainsKey(a.HarvestLotId)) || given.GroupBy(a => a!.HarvestLotId).Any(g => g.Count() > 1))
        {
            return AppError.Invalid(AnswerField);
        }

        var answerOf = given.ToDictionary(a => a!.HarvestLotId, a => a!);
        if (toAsk.Values.FirstOrDefault(l => !answerOf.ContainsKey(l.Id)) is { } unanswered)
        {
            return await QuestionAsync(unanswered, linesByLot[unanswered.Id].Sum(l => l.Kg), cancellationToken);
        }

        var today = timeProvider.Today();
        var reopened = new List<string>();
        var closed = new List<string>();
        foreach (var lot in rows)
        {
            unitOfWork.MarkChanged(lot);
            if (backForSale.Contains(lot.Id))
            {
                // A lot that is already Open or Reopened stays as it is; the kg comes back through the figures.
                if (statuses.Reopen(lot, today, HarvestLotReopenCause.SalesReturnBackForSale))
                {
                    reopened.Add(lot.LotCode);
                }
            }
            else if (answerOf.TryGetValue(lot.Id, out var answer) && !answer.StillForSale)
            {
                if (await closing.CloseWithoutSavingAsync(lot, answer.LeftoverKg, answer.CloseReason, cancellationToken) is { } notClosed)
                {
                    return notClosed;
                }

                closed.Add(lot.LotCode);
            }
        }

        return new SalesReturnLotChanges(reopened, closed);
    }

    private async Task<Result<SalesReturnLotChanges>> QuestionAsync(HarvestLot lot, decimal discardedKg, CancellationToken cancellationToken)
    {
        var figures = await sales.FiguresAsync([lot.Id], cancellationToken);
        var remaining = HarvestLotFigures.Of(lot, figures.GetValueOrDefault(lot.Id, LotSalesFigures.None)).RemainingKg;
        return AppError.Conflict(
            UserMessages.ThrownAwayLotStillForSale(lot.LotCode),
            new Dictionary<string, object?> { ["confirmationField"] = AnswerField, ["harvestLotId"] = lot.Id, ["lotCode"] = lot.LotCode, ["discardedKg"] = discardedKg, ["remainingKg"] = remaining });
    }
}
