using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The weighing sheet of a lot (owner decision of 24/11/2026): every harvest trip of an order is weighed at the port cage by cage, each cage saved as soon as it is weighed, then
/// <see cref="HarvestLotWeighingService"/> completes the weighing from the cages. Weighing, correcting or removing a cage is for a Zone Manager of the lot's zone or the Cooperative Owner
/// (HarvestLots Edit, as the weighing) while the lot is not Closed; it changes neither the lot's kg nor the ledger until the weighing is completed again. Reading the sheet is Harvests View.
/// </summary>
public sealed class HarvestLotCageService(
    IHarvestLotRepository lots,
    HarvestLotViews views,
    LotWeighingSheetBuilder sheets,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string KgField = "Kg";
    public const string ProductField = "Product";

    /// <summary>Two cages weighed at the same time on one lot take the same number; the loser reads the lot again and takes the next one.</summary>
    private const int MaxNumberAttempts = 3;

    /// <summary>The MSG-GEN-01 field of a cage whose product is not, or no longer, a product of the lot.</summary>
    public static string CageProductField(int number) => $"Cage {number} product";

    public async Task<Result<LotWeighingSheet>> SheetAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.View, lot.Raft.ZoneId, cancellationToken)
            ? await SheetOfAsync(lot, cancellationToken)
            : AppError.Forbidden;
    }

    /// <summary>Weighs one more cage: the next number, the product and the kg. A <c>clientOperationId</c> already saved on the lot answers the sheet as it is.</summary>
    public async Task<Result<LotWeighingSheet>> AddAsync(Guid lotId, AddLotCageRequest request, CancellationToken cancellationToken)
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
            if (request.ClientOperationId is { } operation && lot.Cages.Any(c => c.ClientOperationId == operation))
            {
                return await SheetOfAsync(lot, cancellationToken);
            }

            var productId = ProductOf(lot, request.ProductId);
            var check = new FieldCheck();
            ReadCage(lot, productId, request.Kg, check);
            check.Check(lot.Cages.Count < HarvestLotCage.MaxCages, HarvestLotWeighingService.CagesField);
            if (!check.IsValid)
            {
                return check.ToError();
            }

            var cage = new HarvestLotCage
            {
                LotId = lot.Id,
                Side = HarvestLotCageSide.Cooperative,
                Number = lot.Cages.Select(c => c.Number).DefaultIfEmpty(0).Max() + 1,
                ProductId = productId,
                Kg = request.Kg!.Value,
                WeighedAt = timeProvider.GetUtcNow(),
                WeighedByAccountId = Weigher(),
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

    /// <summary>Weighs a cage again, or puts it under another product of the lot; it keeps its number.</summary>
    public async Task<Result<LotWeighingSheet>> UpdateAsync(Guid lotId, Guid cageId, UpdateLotCageRequest request, CancellationToken cancellationToken)
    {
        var editable = await EditableAsync(lotId, cancellationToken);
        if (!editable.IsSuccess)
        {
            return editable.Error!;
        }

        var lot = editable.Value!;
        if (lot.Cages.FirstOrDefault(c => c.Id == cageId) is not { } cage)
        {
            return AppError.NotFound;
        }

        var productId = ProductOf(lot, request.ProductId);
        var check = new FieldCheck();
        ReadCage(lot, productId, request.Kg, check);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        cage.ProductId = productId;
        cage.Kg = request.Kg!.Value;
        cage.WeighedAt = timeProvider.GetUtcNow();
        cage.WeighedByAccountId = Weigher();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SheetOfAsync(lot, cancellationToken);
    }

    /// <summary>Takes a cage off the sheet (weighed by mistake, or twice); its number is not given again.</summary>
    public async Task<Result<LotWeighingSheet>> RemoveAsync(Guid lotId, Guid cageId, CancellationToken cancellationToken)
    {
        var editable = await EditableAsync(lotId, cancellationToken);
        if (!editable.IsSuccess)
        {
            return editable.Error!;
        }

        var lot = editable.Value!;
        if (lot.Cages.FirstOrDefault(c => c.Id == cageId) is not { } cage)
        {
            return AppError.NotFound;
        }

        lot.AllCages.Remove(cage);
        lots.RemoveCage(cage);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SheetOfAsync(lot, cancellationToken);
    }

    /// <summary>The lot tracked for editing its cages: the caller may weigh it (HarvestLots Edit on its zone) and it is not Closed (MSG-GEN-09).</summary>
    private async Task<Result<HarvestLot>> EditableAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.HarvestLots, PermissionAction.Edit, lot.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (lot.StatusId is not (StatusIds.HarvestLot.AwaitingWeighing or StatusIds.HarvestLot.Open or StatusIds.HarvestLot.Reopened))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", statusCatalog.DisplayNameOf(lot.StatusId)));
        }

        return lot;
    }

    /// <summary>
    /// The product of a cage: the one sent, or when none is, the product of the lot's only line. A lot holds one product line since the PM decision of 17/11/2026, so the app sends none; an older lot
    /// with several lines needs it.
    /// </summary>
    private static Guid? ProductOf(HarvestLot lot, Guid? sent) => sent ?? (lot.ProductLines.Count == 1 ? lot.ProductLines.First().ProductId : null);

    /// <summary>MSG-GEN-01 fields of a cage: the kg (above 0, at most 3 decimals) and the product, one of the lot's lines, or none for a lot with no lines.</summary>
    private static void ReadCage(HarvestLot lot, Guid? productId, decimal? kg, FieldCheck check)
    {
        check.Check(InvoiceMoney.IsKg(kg), KgField);
        check.Check(
            lot.ProductLines.Count == 0 ? productId is null : productId is { } id && lot.ProductLines.Any(l => l.ProductId == id),
            ProductField);
    }

    private Guid Weigher() => currentUser.AccountId ?? throw new InvalidOperationException("A cage is weighed by an authenticated caller.");

    private async Task<LotWeighingSheet> SheetOfAsync(HarvestLot lot, CancellationToken cancellationToken)
    {
        var access = await views.AccessAsync(cancellationToken);
        return await sheets.BuildAsync(lot, HarvestLotViews.MayWeigh(access, lot, lot.Raft.ZoneId), traderView: false, cancellationToken);
    }
}
