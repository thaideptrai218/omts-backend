using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The trader's side of the weighing at the port (owner decision of 24/11/2026): the trader of a lot (<see cref="Common.Models.HarvestLotOrders.Trader"/>) weighs every cage beside the cooperative on
/// their own phone, sees the two lists side by side, and agrees to the weighing once the cooperative completed it. The cooperative's cages stay the official weighing: when the two differ the trader
/// agrees to the cooperative's figure, or the cooperative corrects its cages and completes again. A lot of a trader is invoiced only with the agreement (<see cref="HarvestLotAgreementGuard"/>).
/// The API checks that the caller is a trader; a lot that is not theirs answers as not found (only the trader's own data).
/// </summary>
public sealed class TraderWeighingService(
    IHarvestLotRepository lots,
    LotWeighingSheetBuilder sheets,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IStatusCatalog statusCatalog,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string KgField = "Kg";

    private const int MaxPageSize = 100;

    /// <summary>Two cages weighed at the same time on one lot take the same number; the loser reads the lot again and takes the next one.</summary>
    private const int MaxNumberAttempts = 3;

    /// <summary>The trader's lots, the latest harvest first.</summary>
    public async Task<Paged<TraderWeighingRow>> PageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        // The page number is capped so the offset cannot overflow.
        var (pageNumber, size) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var all = await lots.ListOfTraderAsync(Trader(), cancellationToken);
        var shown = all.Skip((pageNumber - 1) * size).Take(size).ToList();
        var orders = await lots.OrdersOfAsync([.. shown.Select(l => l.Id)], cancellationToken);
        var items = shown.Select(lot => new TraderWeighingRow(
            lot.Id, lot.LotCode, lot.HarvestedAt, orders[lot.Id].OrderCodes, lot.ProductLines.OrderBy(l => l.LineNumber).FirstOrDefault()?.Product?.Name,
            lot.Cages.Count, lot.Cages.Sum(c => c.Kg), lot.TraderCages.Count, lot.TraderCages.Sum(c => c.Kg), lot.WeighedKg, lot.WeighedAt, lot.StatusId,
            statusCatalog.DisplayNameOf(lot.StatusId), lot.IsWeighingAgreed, !lot.IsWeighingAgreed)).ToList();
        return new Paged<TraderWeighingRow>(items, pageNumber, size, all.Count);
    }

    public async Task<Result<LotWeighingSheet>> SheetAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        return lot is not null && await IsTraderOfAsync(lot, cancellationToken) ? await SheetOfAsync(lot, cancellationToken) : AppError.NotFound;
    }

    /// <summary>Weighs one more of the trader's cages: the next number and the kg. A <c>clientOperationId</c> already saved on the lot answers the sheet as it is.</summary>
    public async Task<Result<LotWeighingSheet>> AddCageAsync(Guid lotId, AddTraderCageRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Forgets the rows of a lost attempt, so the next one reads the numbers taken meanwhile.
            unitOfWork.DiscardChanges();
            var editable = await EditableAsync(lotId, cancellationToken);
            if (!editable.IsSuccess)
            {
                return editable.Error!;
            }

            var lot = editable.Value!;
            if (request.ClientOperationId is { } operation && lot.TraderCages.Any(c => c.ClientOperationId == operation))
            {
                return await SheetOfAsync(lot, cancellationToken);
            }

            var check = new FieldCheck();
            check.Check(InvoiceMoney.IsKg(request.Kg), KgField);
            check.Check(lot.TraderCages.Count < HarvestLotCage.MaxCages, HarvestLotWeighingService.CagesField);
            if (!check.IsValid)
            {
                return check.ToError();
            }

            var cage = new HarvestLotCage
            {
                LotId = lot.Id,
                Side = HarvestLotCageSide.Trader,
                Number = lot.TraderCages.Select(c => c.Number).DefaultIfEmpty(0).Max() + 1,
                Kg = request.Kg!.Value,
                WeighedAt = timeProvider.GetUtcNow(),
                WeighedByAccountId = Trader(),
                ClientOperationId = request.ClientOperationId,
            };
            lot.AllCages.Add(cage);
            lots.AddCage(cage);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (DuplicateRecordException) when (attempt < MaxNumberAttempts)
            {
                continue;
            }
            catch (DuplicateRecordException exception)
            {
                throw new ConcurrencyConflictException(exception);
            }

            return await SheetOfAsync(lot, cancellationToken);
        }
    }

    /// <summary>Weighs one of the trader's cages again; it keeps its number.</summary>
    public async Task<Result<LotWeighingSheet>> UpdateCageAsync(Guid lotId, Guid cageId, UpdateTraderCageRequest request, CancellationToken cancellationToken)
    {
        var editable = await EditableAsync(lotId, cancellationToken);
        if (!editable.IsSuccess)
        {
            return editable.Error!;
        }

        var lot = editable.Value!;
        if (lot.TraderCages.FirstOrDefault(c => c.Id == cageId) is not { } cage)
        {
            return AppError.NotFound;
        }

        if (!InvoiceMoney.IsKg(request.Kg))
        {
            return AppError.Invalid(KgField);
        }

        cage.Kg = request.Kg!.Value;
        cage.WeighedAt = timeProvider.GetUtcNow();
        cage.WeighedByAccountId = Trader();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SheetOfAsync(lot, cancellationToken);
    }

    /// <summary>Takes one of the trader's cages off the sheet; its number is not given again.</summary>
    public async Task<Result<LotWeighingSheet>> RemoveCageAsync(Guid lotId, Guid cageId, CancellationToken cancellationToken)
    {
        var editable = await EditableAsync(lotId, cancellationToken);
        if (!editable.IsSuccess)
        {
            return editable.Error!;
        }

        var lot = editable.Value!;
        if (lot.TraderCages.FirstOrDefault(c => c.Id == cageId) is not { } cage)
        {
            return AppError.NotFound;
        }

        lot.AllCages.Remove(cage);
        lots.RemoveCage(cage);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SheetOfAsync(lot, cancellationToken);
    }

    /// <summary>
    /// The trader agrees to the cooperative's completed weighing, at the cooperative's kg whatever the trader's own cages say. 409 MSG-GEN-09 while the weighing is not completed, a cage waits for
    /// completing again, or the lot is Closed; MSG06 when the weighing was completed again since the sheet was read. Agreeing again to the same weighing changes nothing.
    /// </summary>
    public async Task<Result<LotWeighingSheet>> AgreeAsync(Guid lotId, AgreeLotWeighingRequest request, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null || !await IsTraderOfAsync(lot, cancellationToken))
        {
            return AppError.NotFound;
        }

        // Before the version: a retry after a lost answer finds the agreement saved, whose save moved the version.
        if (lot.IsWeighingAgreed)
        {
            return await SheetOfAsync(lot, cancellationToken);
        }

        if (await rowVersions.CheckAsync(lot, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (lot.StatusId == StatusIds.HarvestLot.Closed)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", statusCatalog.DisplayNameOf(lot.StatusId)));
        }

        if (lot.WeighedKg is not { } weighedKg || lot.WeighedAt is not { } weighedAt || LotWeighingSheetBuilder.NeedsCompleting(lot, [.. lot.Cages]))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Weighing of harvest lot {lot.LotCode}", "not completed"));
        }

        lot.WeighingAgreedAt = timeProvider.GetUtcNow();
        lot.WeighingAgreedByAccountId = Trader();
        lot.WeighingAgreedKg = weighedKg;
        lot.WeighingAgreedFor = weighedAt;
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.WeighingAgreed, lot, lot.Raft.ZoneId) with
        {
            After = new { lot.WeighingAgreedKg, lot.WeighingAgreedFor, TraderCages = lot.TraderCages.Count, TraderCagesKg = lot.TraderCages.Sum(c => c.Kg) },
        });
        unitOfWork.MarkChanged(lot);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SheetOfAsync(lot, cancellationToken);
    }

    /// <summary>The lot tracked for the trader's cages: the trader's lot (404 otherwise), not Closed (MSG-GEN-09).</summary>
    private async Task<Result<HarvestLot>> EditableAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null || !await IsTraderOfAsync(lot, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (lot.StatusId == StatusIds.HarvestLot.Closed)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", statusCatalog.DisplayNameOf(lot.StatusId)));
        }

        return lot;
    }

    private async Task<bool> IsTraderOfAsync(HarvestLot lot, CancellationToken cancellationToken) =>
        (await lots.OrdersOfAsync([lot.Id], cancellationToken))[lot.Id].Trader?.AccountId == Trader();

    private Task<LotWeighingSheet> SheetOfAsync(HarvestLot lot, CancellationToken cancellationToken) => sheets.BuildAsync(lot, canEdit: true, traderView: true, cancellationToken);

    private Guid Trader() => currentUser.AccountId ?? throw new InvalidOperationException("A trader's weighing needs an authenticated caller.");
}
