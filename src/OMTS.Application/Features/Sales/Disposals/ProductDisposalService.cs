using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Disposals;

/// <summary>Who may do what with the product disposals: the Owner everything; a Zone Manager records, completes, edits and cancels the disposals of its zones; the Accountant reads and exports.</summary>
public sealed class ProductDisposalAccess(ZoneAccessGuard zoneAccess)
{
    /// <summary>What a list may show the caller: the disposals of every zone (null) or of some.</summary>
    public sealed record Scope(bool IsEmpty, IReadOnlySet<Guid>? ZoneIds);

    public Task<bool> IsOwnerAsync(CancellationToken cancellationToken) => zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    public async Task<bool> MayWriteAsync(Guid zoneId, PermissionAction action, CancellationToken cancellationToken) =>
        await IsOwnerAsync(cancellationToken) || await zoneAccess.AllowsAsync(PermissionFunctions.ProductDisposals, action, zoneId, cancellationToken);

    public async Task<bool> MayReadAsync(ProductDisposal disposal, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(PermissionAction.View, cancellationToken);
        return !scope.IsEmpty && (scope.ZoneIds is null || scope.ZoneIds.Contains(disposal.ZoneId));
    }

    public async Task<Scope> ScopeAsync(PermissionAction action, CancellationToken cancellationToken)
    {
        if (await IsOwnerAsync(cancellationToken))
        {
            return new Scope(false, null);
        }

        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.ProductDisposals, action, cancellationToken);
        return new Scope(zones.IsEmpty, zones.AllZones ? null : zones.ZoneIds);
    }
}

/// <summary>The codes of the product disposals: XHS000001, or a code the user types, unique regardless of letter case across every disposal, drafts and cancelled ones included.</summary>
public sealed class ProductDisposalCodeService(IProductDisposalRepository disposals, IDocumentNumberAllocator numbers)
{
    public static string Format(long number) => $"{DocumentSequenceKeys.ProductDisposal}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>The code the next disposal would get, without spending its number.</summary>
    public async Task<string> PeekAsync(CancellationToken cancellationToken)
    {
        var number = await numbers.PeekAsync(DocumentSequenceKeys.ProductDisposal, cancellationToken);
        while (await disposals.FindByCodeAsync(Format(number), null, cancellationToken) is not null)
        {
            number++;
        }

        return Format(number);
    }

    /// <summary>The next generated code; a number whose code someone already typed is spent and skipped.</summary>
    public async Task<string> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = Format(await numbers.NextAsync(DocumentSequenceKeys.ProductDisposal, cancellationToken));
            if (await disposals.FindByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }

    /// <summary>The code of a new disposal: the typed one when free, otherwise (left empty) the next generated one. Call it once the form is valid, so a refused save spends no number.</summary>
    public async Task<(string? Code, AppError? Error)> ForNewAsync(string? typed, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code)
        {
            return (await NextAsync(cancellationToken), null);
        }

        return (code, await DuplicateAsync(code, null, cancellationToken));
    }

    /// <summary>The code after an edit: the stored one when the request leaves it empty or sends it again, otherwise the typed one when free.</summary>
    public async Task<(string? Code, AppError? Error)> ForEditAsync(string? typed, Guid disposalId, string storedCode, CancellationToken cancellationToken)
    {
        if (DocumentCodes.Typed(typed) is not { } code || string.Equals(code, storedCode, StringComparison.Ordinal))
        {
            return (storedCode, null);
        }

        return (code, await DuplicateAsync(code, disposalId, cancellationToken));
    }

    public async Task<AppError?> DuplicateAsync(string code, Guid? exceptDisposalId, CancellationToken cancellationToken) =>
        await disposals.FindByCodeAsync(code, exceptDisposalId, cancellationToken) is { } other ? AppError.AlreadyExists("Product disposal", other.Id, "Code") : null;
}

/// <summary>
/// The disposals of oysters (Xuất hủy hàu, XHS): kg of a product taken from a harvest lot of one zone and written off. A Draft holds nothing and is removed; completing it writes its kg on the product ledger in the same
/// save, and the stock may go below zero (the cooperative writes the harvest down after it sells). A Completed disposal stays editable: its rows are undone and written again. Cancelling one with a reason gives the kg
/// back. The Owner and the Zone Managers of the zone write; the Accountant reads and exports.
/// </summary>
public sealed class ProductDisposalService(
    IProductDisposalRepository disposals,
    IProductRepository products,
    IHarvestLotRepository lots,
    IFarmingZoneRepository zones,
    IAccountRepository accounts,
    ProductDisposalAccess access,
    ProductDisposalCodeService codes,
    ProductStockLedger ledger,
    ISpreadsheetWriter spreadsheets,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(ProductDisposal);
    private const string ObjectName = "Product disposal";
    private const int MaxLines = 200;
    private const int MaxTextLength = 1000;
    private const int MaxNoteLength = 500;
    private const int MaxPageSize = 100;

    // ---- Reading ----

    public async Task<Result<ProductDisposalPage>> SearchAsync(ProductDisposalListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.View, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // The upper bound keeps (page - 1) * pageSize inside int.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var (rows, total, totalKg) = await disposals.SearchAsync(filter.Value!, page, pageSize, cancellationToken);
        return new ProductDisposalPage(await ListItemsAsync(rows, cancellationToken), page, pageSize, total, new ProductDisposalListSummary(totalKg));
    }

    public async Task<Result<ExportedFile>> ExportAsync(ProductDisposalListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.Export, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var rows = await ListItemsAsync(await disposals.ListAsync(filter.Value!, cancellationToken), cancellationToken);
        string[] headers = ["Code", "Status", "Disposed at", "Zone", "Reason", "Notes", "Total kg", "Created by", "Created at"];
        var data = rows.Select(r => (IReadOnlyList<object?>)new List<object?>
        {
            r.Code, r.Status, r.DisposedAt.ToOffset(BusinessClock.UtcOffset).DateTime, r.ZoneCode, r.Reason, r.Notes, r.TotalKg, r.CreatedBy, r.CreatedAt.ToOffset(BusinessClock.UtcOffset).DateTime,
        }).ToList();
        try
        {
            return new ExportedFile($"product-disposals-{timeProvider.Today():yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, spreadsheets.Write([new SpreadsheetSheet("Disposals", headers, data)]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    public async Task<Result<ProductDisposalView>> GetAsync(Guid disposalId, CancellationToken cancellationToken)
    {
        var disposal = await disposals.GetForEditAsync(disposalId, cancellationToken);
        return disposal is null || !await access.MayReadAsync(disposal, cancellationToken) ? AppError.NotFound : await ViewAsync(disposal, cancellationToken);
    }

    /// <summary>The code the next disposal would get; not reserved.</summary>
    public async Task<Result<ProductDisposalNextCode>> NextCodeAsync(CancellationToken cancellationToken) =>
        !(await access.ScopeAsync(PermissionAction.Create, cancellationToken)).IsEmpty ? new ProductDisposalNextCode(await codes.PeekAsync(cancellationToken)) : AppError.Forbidden;

    // ---- Writing ----

    public async Task<Result<ProductDisposalView>> CreateAsync(SaveProductDisposalRequest request, CancellationToken cancellationToken)
    {
        if (request.ZoneId is not { } zoneId)
        {
            return AppError.InvalidFields(["Zone"]);
        }

        if (!await access.MayWriteAsync(zoneId, PermissionAction.Create, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var (prepared, error) = await PrepareAsync(request, stored: null, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (code, codeError) = await codes.ForNewAsync(request.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var disposal = new ProductDisposal { Code = code! };
        Apply(disposal, prepared!, replaceLines: true);
        disposals.Add(disposal);
        audit.Record(new AuditEntry("ProductDisposal.Created", RecordType) { RecordId = disposal.Id, RecordCode = disposal.Code, ZoneId = disposal.ZoneId, After = Snapshot(disposal) });
        if (request.Complete)
        {
            Complete(disposal);
        }

        if (await SaveAsync(disposal, cancellationToken) is { } saveError)
        {
            return saveError;
        }

        return await ViewAsync(await disposals.GetForEditAsync(disposal.Id, cancellationToken) ?? disposal, cancellationToken);
    }

    /// <summary>Replaces the header and the lines of a Draft (with <c>complete</c> it is completed in the same call) or of a Completed disposal (its kg are written again). A Cancelled disposal is never changed.</summary>
    public async Task<Result<ProductDisposalView>> UpdateAsync(Guid disposalId, SaveProductDisposalRequest request, CancellationToken cancellationToken)
    {
        var disposal = await disposals.GetForEditAsync(disposalId, cancellationToken);
        if (disposal is null || !await access.MayReadAsync(disposal, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (disposal.StatusId == StatusIds.ProductDisposal.Cancelled)
        {
            return WrongStatus(disposal);
        }

        if (!await access.MayWriteAsync(disposal.ZoneId, PermissionAction.Edit, cancellationToken)
            || (request.ZoneId is { } newZone && newZone != disposal.ZoneId && !await access.MayWriteAsync(newZone, PermissionAction.Edit, cancellationToken)))
        {
            return AppError.Forbidden;
        }

        // An edit replaces every line, so it must say which version it read.
        if (await rowVersions.CheckAsync(disposal, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return await ReplaceAsync(disposal, request, cancellationToken);
    }

    /// <summary>Draft → Completed as stored; the row version is optional because the status guards a second complete.</summary>
    public async Task<Result<ProductDisposalView>> CompleteAsync(Guid disposalId, CompleteProductDisposalRequest request, CancellationToken cancellationToken)
    {
        var disposal = await disposals.GetForEditAsync(disposalId, cancellationToken);
        if (disposal is null || !await access.MayReadAsync(disposal, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayWriteAsync(disposal.ZoneId, PermissionAction.Edit, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (disposal.StatusId != StatusIds.ProductDisposal.Draft)
        {
            return WrongStatus(disposal);
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(disposal, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return await ReplaceAsync(disposal, ToRequest(disposal) with { Complete = true }, cancellationToken);
    }

    /// <summary>Completed → Cancelled with a reason: the kg go back in the stock.</summary>
    public async Task<Result<ProductDisposalView>> CancelAsync(Guid disposalId, CancelProductDisposalRequest request, CancellationToken cancellationToken)
    {
        var disposal = await disposals.GetForEditAsync(disposalId, cancellationToken);
        if (disposal is null || !await access.MayReadAsync(disposal, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayWriteAsync(disposal.ZoneId, PermissionAction.Delete, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (disposal.StatusId != StatusIds.ProductDisposal.Completed)
        {
            return WrongStatus(disposal);
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > MaxTextLength)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(disposal, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        disposal.StatusId = StatusIds.ProductDisposal.Cancelled;
        disposal.CancelledAt = timeProvider.GetUtcNow();
        disposal.CancelReason = reason;
        unitOfWork.MarkChanged(disposal);
        audit.Record(new AuditEntry("ProductDisposal.Cancelled", RecordType)
        {
            RecordId = disposal.Id,
            RecordCode = disposal.Code,
            ZoneId = disposal.ZoneId,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.ProductDisposal.Completed) },
            After = new { Status = statusCatalog.CodeOf(disposal.StatusId) },
            Reason = reason,
        });
        await ledger.ReverseAsync(disposal.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(disposal, cancellationToken);
    }

    /// <summary>Removes a Draft and its lines; the removal stays in the audit log only.</summary>
    public async Task<Result<Done>> RemoveAsync(Guid disposalId, CancellationToken cancellationToken)
    {
        var disposal = await disposals.GetForEditAsync(disposalId, cancellationToken);
        if (disposal is null || !await access.MayReadAsync(disposal, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayWriteAsync(disposal.ZoneId, PermissionAction.Delete, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (disposal.StatusId != StatusIds.ProductDisposal.Draft)
        {
            // A Completed disposal is cancelled with a reason, never removed.
            return WrongStatus(disposal);
        }

        var before = Snapshot(disposal);
        foreach (var line in disposal.Lines.ToList())
        {
            disposal.Lines.Remove(line);
            disposals.RemoveLine(line);
        }

        disposals.Remove(disposal);
        audit.Record(new AuditEntry("ProductDisposal.Removed", RecordType) { RecordId = disposal.Id, RecordCode = disposal.Code, ZoneId = disposal.ZoneId, Before = before });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    // ---- Preparing a save ----

    private sealed record PreparedLine(Guid ProductId, HarvestLot Lot, decimal Kg, string? Note);

    private sealed record Prepared(DateTimeOffset DisposedAt, FarmingZone Zone, string? Reason, string? Notes, IReadOnlyList<PreparedLine> Lines);

    /// <summary>Checks a request against the catalogue and the harvest lots and returns what it names; MSG-GEN-01 names every invalid field.</summary>
    private async Task<(Prepared? Prepared, AppError? Error)> PrepareAsync(SaveProductDisposalRequest request, ProductDisposal? stored, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        var now = timeProvider.GetUtcNow();
        var disposedAt = request.DisposedAt?.ToOffset(BusinessClock.UtcOffset) ?? stored?.DisposedAt ?? now.ToOffset(BusinessClock.UtcOffset);
        if (request.DisposedAt is { } at && at > now + PurchaseReceiptValidator.ClockTolerance)
        {
            invalid.Add("Disposed at");
        }

        var reason = request.Reason?.Trim();
        var notes = request.Notes?.Trim();
        if (reason?.Length > MaxTextLength)
        {
            invalid.Add("Reason");
        }

        if (notes?.Length > MaxTextLength)
        {
            invalid.Add("Notes");
        }

        FarmingZone? zone = null;
        if (request.ZoneId is { } zoneId)
        {
            zone = (await zones.GetByIdsAsync([zoneId], cancellationToken)).FirstOrDefault();
            if (zone is null || (zone.StatusId != StatusIds.Usage.InUse && zone.Id != stored?.ZoneId))
            {
                invalid.Add("Zone");
                zone = null;
            }
        }
        else
        {
            invalid.Add("Zone");
        }

        var inputs = request.Lines ?? [];
        if (inputs.Count > MaxLines || inputs.Any(l => l is null))
        {
            invalid.Add("Lines");
            return (null, AppError.InvalidFields(invalid.Distinct().ToList()));
        }

        var foundProducts = (await products.GetByIdsAsync(inputs.Select(l => l.ProductId).OfType<Guid>().Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var foundLots = (await lots.ListForInvoiceAsync(inputs.Select(l => l.HarvestLotId).OfType<Guid>().Distinct().ToList(), cancellationToken)).ToDictionary(l => l.Id);
        var lines = new List<PreparedLine>();
        var storedProducts = stored?.Lines.Select(l => l.ProductId).ToHashSet() ?? [];
        var seen = new HashSet<(Guid, Guid)>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var field = $"Line {i + 1}";
            // A product deleted (hidden) since stays allowed on the disposal that already has it, so that disposal can still be edited.
            if (input.ProductId is not { } productId || !foundProducts.TryGetValue(productId, out var product) || (product.IsHidden && !storedProducts.Contains(productId)))
            {
                invalid.Add($"{field} product");
                continue;
            }

            // The lot is one of the zone of the disposal, and was split into the product (a lot recorded before products has no lines and holds no stock).
            if (input.HarvestLotId is not { } lotId || !foundLots.TryGetValue(lotId, out var lot) || (zone is not null && lot.Raft.ZoneId != zone.Id) || lot.ProductLines.All(l => l.ProductId != productId))
            {
                invalid.Add($"{field} lot");
                continue;
            }

            if (input.Kg is not { } kg || kg <= 0 || kg > PurchaseReceiptValidator.MaxQuantity || decimal.Round(kg, 3) != kg)
            {
                invalid.Add($"{field} kg");
                continue;
            }

            if (!seen.Add((productId, lotId)))
            {
                invalid.Add($"{field} lot");
                continue;
            }

            var note = input.Note?.Trim();
            if (note?.Length > MaxNoteLength)
            {
                invalid.Add($"{field} note");
            }

            lines.Add(new PreparedLine(productId, lot, kg, string.IsNullOrEmpty(note) ? null : note));
        }

        if (invalid.Count > 0)
        {
            return (null, AppError.InvalidFields(invalid.Distinct().ToList()));
        }

        if (request.Complete || stored is { StatusId: StatusIds.ProductDisposal.Completed })
        {
            var missing = new List<string>();
            if (lines.Count == 0)
            {
                missing.Add("Lines");
            }

            if (string.IsNullOrEmpty(reason))
            {
                missing.Add("Reason");
            }

            if (missing.Count > 0)
            {
                return (null, AppError.InvalidFields(missing));
            }
        }

        return (new Prepared(disposedAt, zone!, string.IsNullOrEmpty(reason) ? null : reason, string.IsNullOrEmpty(notes) ? null : notes, lines), null);
    }

    // ---- Applying a save ----

    /// <summary>The save of an existing disposal, after the checks of who may and which version it read.</summary>
    private async Task<Result<ProductDisposalView>> ReplaceAsync(ProductDisposal disposal, SaveProductDisposalRequest request, CancellationToken cancellationToken)
    {
        var (prepared, error) = await PrepareAsync(request, disposal, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (code, codeError) = await codes.ForEditAsync(request.Code, disposal.Id, disposal.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var wasCompleted = disposal.StatusId == StatusIds.ProductDisposal.Completed;
        var before = Snapshot(disposal);
        var ledgerBefore = LedgerKey(disposal.Code, disposal.DisposedAt, disposal.Reason, disposal.Lines.Select(l => (l.ProductId, l.HarvestLotId, l.Kg)));
        var linesBefore = LineKey(disposal.Lines.OrderBy(l => l.LineNumber).Select(l => (l.ProductId, l.HarvestLotId, l.Kg, l.Note)));
        var newLines = LineKey(prepared!.Lines.Select(l => (l.ProductId, l.Lot.Id, l.Kg, l.Note)));
        disposal.Code = code!;
        Apply(disposal, prepared, replaceLines: linesBefore != newLines);
        unitOfWork.MarkChanged(disposal);
        var ledgerAfter = LedgerKey(disposal.Code, disposal.DisposedAt, disposal.Reason, disposal.Lines.Select(l => (l.ProductId, l.HarvestLotId, l.Kg)));
        if (wasCompleted)
        {
            if (ledgerAfter != ledgerBefore)
            {
                await ledger.ReverseAsync(disposal.Id, cancellationToken);
                ledger.PostDisposal(disposal, disposal.Lines);
            }
        }
        else if (request.Complete)
        {
            Complete(disposal);
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(disposal));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry(wasCompleted ? "ProductDisposal.Edited" : "ProductDisposal.Updated", RecordType)
            {
                RecordId = disposal.Id, RecordCode = disposal.Code, ZoneId = disposal.ZoneId, Before = changedBefore, After = changedAfter,
            });
        }

        if (await SaveAsync(disposal, cancellationToken) is { } saveError)
        {
            return saveError;
        }

        return await ViewAsync(await disposals.GetForEditAsync(disposal.Id, cancellationToken) ?? disposal, cancellationToken);
    }

    /// <summary>Draft → Completed: the status and the kg on the ledger, before the one save.</summary>
    private void Complete(ProductDisposal disposal)
    {
        var from = disposal.StatusId;
        disposal.StatusId = StatusIds.ProductDisposal.Completed;
        disposal.CompletedAt = timeProvider.GetUtcNow();
        audit.RecordStatusChange(RecordType, disposal.Id, disposal.Code, from, StatusIds.ProductDisposal.Completed, zoneId: disposal.ZoneId);
        ledger.PostDisposal(disposal, disposal.Lines);
    }

    private void Apply(ProductDisposal disposal, Prepared prepared, bool replaceLines)
    {
        disposal.DisposedAt = prepared.DisposedAt;
        disposal.ZoneId = prepared.Zone.Id;
        disposal.Reason = prepared.Reason;
        disposal.Notes = prepared.Notes;
        if (!replaceLines)
        {
            return;
        }

        foreach (var line in disposal.Lines.ToList())
        {
            disposal.Lines.Remove(line);
            disposals.RemoveLine(line);
        }

        var number = 0;
        foreach (var input in prepared.Lines)
        {
            // The product is named by id only (an untracked read stays out of the graph); the lot is a tracked entity and gives its code to the ledger rows.
            var line = new ProductDisposalLine { DisposalId = disposal.Id, LineNumber = ++number, ProductId = input.ProductId, HarvestLotId = input.Lot.Id, HarvestLot = input.Lot, Kg = input.Kg, Note = input.Note };
            disposal.Lines.Add(line);
            disposals.AddLine(line);
        }
    }

    private static string LedgerKey(string code, DateTimeOffset disposedAt, string? reason, IEnumerable<(Guid ProductId, Guid LotId, decimal Kg)> lines) =>
        $"{code}@{disposedAt:O}@{reason}@" + string.Join(";", lines.OrderBy(l => l.ProductId).ThenBy(l => l.LotId).ThenBy(l => l.Kg).Select(l => $"{l.ProductId}|{l.LotId}|{Kg(l.Kg)}"));

    /// <summary>Kg compare by value: a stored 20.000 and a typed 20 are the same quantity.</summary>
    private static string Kg(decimal kg) => kg.ToString("0.###", CultureInfo.InvariantCulture);

    private static string LineKey(IEnumerable<(Guid ProductId, Guid LotId, decimal Kg, string? Note)> lines) =>
        string.Join(";", lines.Select(l => $"{l.ProductId}|{l.LotId}|{Kg(l.Kg)}|{l.Note}"));

    private static SaveProductDisposalRequest ToRequest(ProductDisposal disposal) => new(
        disposal.Code, disposal.DisposedAt, disposal.ZoneId, disposal.Reason, disposal.Notes,
        disposal.Lines.OrderBy(l => l.LineNumber).Select(l => new ProductDisposalLineInput(l.ProductId, l.HarvestLotId, l.Kg, l.Note)).ToList());

    /// <summary>The second of two parallel saves with one code gets the same MSG-GEN-02 as the pre-check.</summary>
    private async Task<AppError?> SaveAsync(ProductDisposal disposal, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return await codes.DuplicateAsync(disposal.Code, disposal.Id, cancellationToken) ?? AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }
    }

    private static object Snapshot(ProductDisposal d) => new
    {
        d.Code, d.DisposedAt, d.ZoneId, d.Reason, d.Notes,
        Lines = d.Lines.OrderBy(l => l.LineNumber).Select(l => new { l.LineNumber, l.ProductId, l.HarvestLotId, l.Kg, l.Note }).ToList(),
    };

    private AppError WrongStatus(ProductDisposal disposal) =>
        AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {disposal.Code}", statusCatalog.DisplayNameOf(disposal.StatusId)));

    // ---- Views and filters ----

    private async Task<Result<ProductDisposalFilter>> FilterAsync(ProductDisposalListQuery query, PermissionAction action, CancellationToken cancellationToken)
    {
        var scope = await access.ScopeAsync(action, cancellationToken);
        if (scope.IsEmpty)
        {
            return AppError.Forbidden;
        }

        ProductDisposalSort? sort = query.SortBy switch
        {
            null or "code" => ProductDisposalSort.Code,
            "disposedAt" => ProductDisposalSort.DisposedAt,
            "totalKg" => ProductDisposalSort.TotalKg,
            "status" => ProductDisposalSort.Status,
            _ => null,
        };
        if (sort is null)
        {
            return AppError.Invalid("Sort by");
        }

        if (query.SortDir is not (null or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction");
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        if (!Enum.IsDefined(query.Status))
        {
            return AppError.Invalid("Status");
        }

        IReadOnlyList<int>? statusIds = query.Status switch
        {
            ProductDisposalStatusFilter.Draft => [StatusIds.ProductDisposal.Draft],
            ProductDisposalStatusFilter.Completed => [StatusIds.ProductDisposal.Completed],
            ProductDisposalStatusFilter.Cancelled => [StatusIds.ProductDisposal.Cancelled],
            _ => null,
        };
        var text = DisplayNames.Normalize(query.Search);
        return new ProductDisposalFilter(
            text.Length == 0 ? null : text, statusIds,
            query.From is { } from ? BusinessClock.FromLocal(from.ToDateTime(TimeOnly.MinValue)) : null,
            query.To is { } to ? BusinessClock.FromLocal(to.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null,
            query.ZoneId, query.ProductId, query.CreatedById, scope.ZoneIds, sort.Value, query.SortDir != "asc");
    }

    private async Task<IReadOnlyList<ProductDisposalListItem>> ListItemsAsync(IReadOnlyList<ProductDisposalRow> rows, CancellationToken cancellationToken)
    {
        var creators = rows.Select(r => r.Disposal.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = creators.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(creators, cancellationToken);
        return rows.Select(r =>
        {
            var d = r.Disposal;
            return new ProductDisposalListItem(
                d.Id, d.Code, d.StatusId, statusCatalog.DisplayNameOf(d.StatusId), d.DisposedAt, d.ZoneId, d.Zone.Code, d.Reason, d.Notes, r.TotalKg, d.CreatedAt,
                d.CreatedBy is { } id ? names.GetValueOrDefault(id) : null, RowVersionGuard.Encode(d.RowVersion));
        }).ToList();
    }

    private async Task<ProductDisposalView> ViewAsync(ProductDisposal d, CancellationToken cancellationToken)
    {
        var creator = d.CreatedBy is { } createdBy ? (await accounts.GetDisplayNamesAsync([createdBy], cancellationToken)).GetValueOrDefault(createdBy) : null;
        // A line that was just saved names its product by id only: the codes are read from the catalogue.
        var catalogue = d.Lines.Count == 0 ? new Dictionary<Guid, Product>() : (await products.GetByIdsAsync(d.Lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var lines = d.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var product = l.Product ?? catalogue.GetValueOrDefault(l.ProductId);
            return new ProductDisposalLineView(l.Id, l.LineNumber, l.ProductId, product?.Code ?? string.Empty, product?.Name ?? string.Empty, l.HarvestLotId, l.HarvestLot?.LotCode ?? string.Empty, l.Kg, l.Note);
        }).ToList();
        return new ProductDisposalView(
            d.Id, d.Code, d.StatusId, statusCatalog.DisplayNameOf(d.StatusId), d.DisposedAt, d.ZoneId, d.Zone?.Code ?? string.Empty, d.Reason, d.Notes, lines, lines.Sum(l => l.Kg),
            d.CreatedAt, creator, d.CompletedAt, d.CancelledAt, d.CancelReason, RowVersionGuard.Encode(d.RowVersion));
    }
}
