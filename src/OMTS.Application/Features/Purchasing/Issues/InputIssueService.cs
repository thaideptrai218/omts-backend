using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
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
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Issues;

/// <summary>Who may do what with the input issues: the Owner everything; a Zone Manager works on the use issues of its zones; the Accountant reads (and exports) every issue; disposals are written by the Owner only.</summary>
public sealed class InputIssueAccess(ZoneAccessGuard zoneAccess)
{
    /// <summary>What a list may show the caller: the use issues of all zones (null) or of some, and the disposals or not.</summary>
    public sealed record Scope(bool IncludeUse, IReadOnlySet<Guid>? UseZoneIds, bool IncludeDisposals)
    {
        public bool IsEmpty => !IncludeUse && !IncludeDisposals;
    }

    public Task<bool> IsOwnerAsync(CancellationToken cancellationToken) => zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    public async Task<bool> MayWriteAsync(InputIssueKind kind, Guid? zoneId, PermissionAction action, CancellationToken cancellationToken)
    {
        if (await IsOwnerAsync(cancellationToken))
        {
            return true;
        }

        return kind == InputIssueKind.Use && zoneId is { } zone && await zoneAccess.AllowsAsync(PermissionFunctions.InputIssues, action, zone, cancellationToken);
    }

    public async Task<bool> MayReadAsync(InputIssue issue, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(PermissionAction.View, cancellationToken);
        return issue.Kind == InputIssueKind.Disposal
            ? scope.IncludeDisposals
            : scope.IncludeUse && (scope.UseZoneIds is null || (issue.ZoneId is { } zone && scope.UseZoneIds.Contains(zone)));
    }

    public async Task<Scope> ScopeAsync(PermissionAction action, CancellationToken cancellationToken)
    {
        if (await IsOwnerAsync(cancellationToken))
        {
            return new Scope(true, null, true);
        }

        var use = await zoneAccess.PermittedZonesAsync(PermissionFunctions.InputIssues, action, cancellationToken);
        var disposals = await zoneAccess.PermittedZonesAsync(PermissionFunctions.InputDisposals, action, cancellationToken);
        return new Scope(!use.IsEmpty, use.AllZones ? null : use.ZoneIds, !disposals.IsEmpty);
    }
}

/// <summary>
/// The issues of input items: a use in a zone (XD) or a disposal (XH). A Draft holds nothing and is removed; completing it writes its quantities on the movement ledger in the same save,
/// and when the cooperative does not allow negative stock it fails (MSG-INPUT-01) if an item, or a seed lot, would go below zero. A Completed issue stays editable: its rows are undone and
/// written again. A Seed line names a seed lot of the item, or none to draw from the quantity of the item that has no lot. The cost of a line is a snapshot of the item cost when the issue
/// was completed or its lines changed.
/// </summary>
public sealed class InputIssueService(
    IInputIssueRepository issues,
    IInputIssueTypeRepository types,
    IFarmingInputItemRepository items,
    IInputStockRepository stock,
    IFarmingZoneRepository zones,
    IRaftRepository rafts,
    IAccountRepository accounts,
    InputIssueAccess access,
    InputIssueCodeService codes,
    InputStockLedger ledger,
    ISpreadsheetWriter spreadsheets,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(InputIssue);
    private const string ObjectName = "Input issue";
    private const int MaxLines = 200;
    private const int MaxTextLength = 1000;
    private const int MaxNoteLength = 500;
    private const int MaxPageSize = 100;

    // ---- Reading ----

    public async Task<Result<InputIssuePage>> SearchAsync(InputIssueListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.View, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // The upper bound keeps (page - 1) * pageSize inside int.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var (rows, total, totalValue) = await issues.SearchAsync(filter.Value!, page, pageSize, cancellationToken);
        return new InputIssuePage(await ListItemsAsync(rows, cancellationToken), page, pageSize, total, new InputIssueListSummary(totalValue));
    }

    public async Task<Result<ExportedFile>> ExportAsync(InputIssueListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.Export, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var rows = await ListItemsAsync(await issues.ListAsync(filter.Value!, cancellationToken), cancellationToken);
        string[] headers = ["Code", "Type", "Status", "Issued at", "Zone", "Raft", "Issue type", "Receiver", "Reason", "Notes", "Total value", "Created by", "Created at"];
        var data = rows.Select(r => (IReadOnlyList<object?>)new List<object?>
        {
            r.Code, r.Kind.ToString(), r.Status, r.IssuedAt.ToOffset(BusinessClock.UtcOffset).DateTime, r.ZoneCode, r.RaftCode, r.IssueTypeName, r.ReceiverName, r.Reason, r.Notes,
            r.TotalValue, r.CreatedBy, r.CreatedAt.ToOffset(BusinessClock.UtcOffset).DateTime,
        }).ToList();
        try
        {
            return new ExportedFile($"input-issues-{timeProvider.Today():yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, spreadsheets.Write([new SpreadsheetSheet("Issues", headers, data)]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    public async Task<Result<InputIssueView>> GetAsync(Guid issueId, CancellationToken cancellationToken)
    {
        var issue = await issues.GetForEditAsync(issueId, cancellationToken);
        return issue is null || !await access.MayReadAsync(issue, cancellationToken) ? AppError.NotFound : await ViewAsync(issue, cancellationToken);
    }

    /// <summary>The code the next issue of the kind would get; not reserved.</summary>
    public async Task<Result<InputIssueNextCode>> NextCodeAsync(InputIssueKind kind, CancellationToken cancellationToken) =>
        await access.MayWriteAsync(kind, null, PermissionAction.Create, cancellationToken) || (kind == InputIssueKind.Use && !(await access.ScopeAsync(PermissionAction.Create, cancellationToken)).IsEmpty)
            ? new InputIssueNextCode(await codes.PeekAsync(kind, cancellationToken))
            : AppError.Forbidden;

    /// <summary>The employees who work in the zone, for the receiver of a use.</summary>
    public async Task<Result<IReadOnlyList<InputIssueReceiverView>>> ReceiversAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var scope = await access.ScopeAsync(PermissionAction.View, cancellationToken);
        if (!scope.IncludeUse || (scope.UseZoneIds is not null && !scope.UseZoneIds.Contains(zoneId)))
        {
            return AppError.Forbidden;
        }

        return Result<IReadOnlyList<InputIssueReceiverView>>.Success(
            (await issues.ReceiversAsync(zoneId, cancellationToken)).Select(r => new InputIssueReceiverView(r.Id, r.FullName)).ToList());
    }

    // ---- Writing ----

    public async Task<Result<InputIssueView>> CreateAsync(SaveInputIssueRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            return AppError.InvalidFields(["Kind"]);
        }

        if (kind == InputIssueKind.Use && request.ZoneId is null && !await access.IsOwnerAsync(cancellationToken))
        {
            return AppError.InvalidFields(["Zone"]);
        }

        if (!await access.MayWriteAsync(kind, request.ZoneId, PermissionAction.Create, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var (prepared, error) = await PrepareAsync(request, kind, stored: null, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (code, codeError) = await codes.ForNewAsync(request.Code, kind, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var issue = new InputIssue { Kind = kind, Code = code! };
        Apply(issue, prepared!, replaceLines: false);
        issues.Add(issue);
        audit.Record(new AuditEntry("InputIssue.Created", RecordType) { RecordId = issue.Id, RecordCode = issue.Code, After = Snapshot(issue) });
        if (request.Complete && await CompleteAsync(issue, prepared!, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (await SaveAsync(issue, cancellationToken) is { } saveError)
        {
            return saveError;
        }

        return await ViewAsync(await issues.GetForEditAsync(issue.Id, cancellationToken) ?? issue, cancellationToken);
    }

    /// <summary>Replaces the header and the lines of a Draft (with <c>complete</c> it is completed in the same call) or of a Completed issue (its quantities are written again). A Cancelled issue is never changed.</summary>
    public async Task<Result<InputIssueView>> UpdateAsync(Guid issueId, SaveInputIssueRequest request, CancellationToken cancellationToken)
    {
        var issue = await issues.GetForEditAsync(issueId, cancellationToken);
        if (issue is null || !await access.MayReadAsync(issue, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (issue.StatusId == StatusIds.InputIssue.Cancelled)
        {
            return WrongStatus(issue);
        }

        if (request.Kind is { } kind && kind != issue.Kind)
        {
            return AppError.InvalidFields(["Kind"]);
        }

        if (!await access.MayWriteAsync(issue.Kind, issue.ZoneId, PermissionAction.Edit, cancellationToken)
            || (request.ZoneId != issue.ZoneId && request.ZoneId is not null && !await access.MayWriteAsync(issue.Kind, request.ZoneId, PermissionAction.Edit, cancellationToken)))
        {
            return AppError.Forbidden;
        }

        // An edit replaces every line, so it must say which version it read.
        if (await rowVersions.CheckAsync(issue, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return await ReplaceAsync(issue, request, cancellationToken);
    }

    /// <summary>Draft → Completed as stored; the row version is optional because the status guards a second complete.</summary>
    public async Task<Result<InputIssueView>> CompleteAsync(Guid issueId, CompleteInputIssueRequest request, CancellationToken cancellationToken)
    {
        var issue = await issues.GetForEditAsync(issueId, cancellationToken);
        if (issue is null || !await access.MayReadAsync(issue, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayWriteAsync(issue.Kind, issue.ZoneId, PermissionAction.Edit, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (issue.StatusId != StatusIds.InputIssue.Draft)
        {
            return WrongStatus(issue);
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(issue, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return await ReplaceAsync(issue, ToRequest(issue) with { Complete = true }, cancellationToken);
    }

    /// <summary>Completed → Cancelled with a reason: the quantities go back in the stock.</summary>
    public async Task<Result<InputIssueView>> CancelAsync(Guid issueId, CancelInputIssueRequest request, CancellationToken cancellationToken)
    {
        var issue = await issues.GetForEditAsync(issueId, cancellationToken);
        if (issue is null || !await access.MayReadAsync(issue, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayWriteAsync(issue.Kind, issue.ZoneId, PermissionAction.Delete, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (issue.StatusId != StatusIds.InputIssue.Completed)
        {
            return WrongStatus(issue);
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > MaxTextLength)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(issue, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        issue.StatusId = StatusIds.InputIssue.Cancelled;
        issue.CancelledAt = timeProvider.GetUtcNow();
        issue.CancelReason = reason;
        unitOfWork.MarkChanged(issue);
        audit.Record(new AuditEntry("InputIssue.Cancelled", RecordType)
        {
            RecordId = issue.Id,
            RecordCode = issue.Code,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.InputIssue.Completed) },
            After = new { Status = statusCatalog.CodeOf(issue.StatusId) },
            Reason = reason,
        });
        await ledger.ReverseAsync(issue.Id, afterReceiptRemoved: false, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(issue, cancellationToken);
    }

    /// <summary>Removes a Draft and its lines; the removal stays in the audit log only.</summary>
    public async Task<Result<Done>> RemoveAsync(Guid issueId, CancellationToken cancellationToken)
    {
        var issue = await issues.GetForEditAsync(issueId, cancellationToken);
        if (issue is null || !await access.MayReadAsync(issue, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayWriteAsync(issue.Kind, issue.ZoneId, PermissionAction.Delete, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (issue.StatusId != StatusIds.InputIssue.Draft)
        {
            // A Completed issue is cancelled with a reason, never removed.
            return WrongStatus(issue);
        }

        var before = Snapshot(issue);
        foreach (var line in issue.Lines.ToList())
        {
            issue.Lines.Remove(line);
            issues.RemoveLine(line);
        }

        issues.Remove(issue);
        audit.Record(new AuditEntry("InputIssue.Removed", RecordType) { RecordId = issue.Id, RecordCode = issue.Code, Before = before });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    /// <summary>
    /// A Completed disposal that writes off the given quantities (an item, or a seed lot of it, being deleted). It is added to the unit of work and not saved: the caller saves it with
    /// what it changes. It never fails for a negative stock, as it only takes out what is there.
    /// </summary>
    internal async Task<InputIssue> RecordDisposalAsync(IReadOnlyList<(FarmingInputItem Item, Guid? LotId, decimal Quantity)> parts, string reason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToOffset(BusinessClock.UtcOffset);
        var issue = new InputIssue
        {
            Kind = InputIssueKind.Disposal,
            Code = await codes.NextAsync(InputIssueKind.Disposal, cancellationToken),
            IssuedAt = now,
            Reason = reason,
            StatusId = StatusIds.InputIssue.Completed,
            CompletedAt = now,
        };
        var number = 0;
        foreach (var (item, lotId, quantity) in parts)
        {
            var unit = item.Units.FirstOrDefault(u => u.IsBase);
            var line = new InputIssueLine { IssueId = issue.Id, LineNumber = ++number, ItemId = item.Id, SeedLotLineId = lotId };
            SetLine(line, item, unit, quantity, 1m);
            issue.Lines.Add(line);
            issues.AddLine(line);
        }

        issues.Add(issue);
        audit.Record(new AuditEntry("InputIssue.Created", RecordType) { RecordId = issue.Id, RecordCode = issue.Code, After = Snapshot(issue), Reason = reason });
        await ledger.PostIssueAsync(
            InputStockMovementKind.Disposal, issue.Id, issue.Code, issue.IssuedAt, reason,
            issue.Lines.Select(l => new StockPosting(l.ItemId, l.SeedLotLineId, -l.Quantity, null)).ToList(), cancellationToken);
        return issue;
    }

    // ---- Preparing a save ----

    private sealed record PreparedLine(FarmingInputItem Item, InputItemUnit? Unit, decimal EnteredQuantity, Guid? LotId, string? Note);

    private sealed record Prepared(
        InputIssueKind Kind, DateTimeOffset IssuedAt, FarmingZone? Zone, Raft? Raft, InputIssueType? Type, Guid? ReceiverId, string? ReceiverName, string? Reason, string? Notes,
        IReadOnlyList<PreparedLine> Lines, string Party);

    /// <summary>Checks a request against the catalogue and returns what it names; MSG-GEN-01 names the first invalid field, <c>fields</c> all of them.</summary>
    private async Task<(Prepared? Prepared, AppError? Error)> PrepareAsync(SaveInputIssueRequest request, InputIssueKind kind, InputIssue? stored, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        var now = timeProvider.GetUtcNow();
        var issuedAt = request.IssuedAt?.ToOffset(BusinessClock.UtcOffset) ?? stored?.IssuedAt ?? now.ToOffset(BusinessClock.UtcOffset);
        if (request.IssuedAt is { } at && at > now + PurchaseReceiptValidator.ClockTolerance)
        {
            invalid.Add("Issued at");
        }

        var reason = request.Reason?.Trim();
        var notes = request.Notes?.Trim();
        if (reason?.Length > MaxTextLength || (kind == InputIssueKind.Use && !string.IsNullOrEmpty(reason)))
        {
            invalid.Add("Reason");
        }

        if (notes?.Length > MaxTextLength)
        {
            invalid.Add("Notes");
        }

        FarmingZone? zone = null;
        Raft? raft = null;
        InputIssueType? type = null;
        string? receiverName = null;
        if (kind == InputIssueKind.Use)
        {
            if (request.ZoneId is { } zoneId)
            {
                zone = (await zones.GetByIdsAsync([zoneId], cancellationToken)).FirstOrDefault();
                if (zone is not { StatusId: StatusIds.Usage.InUse })
                {
                    invalid.Add("Zone");
                    zone = null;
                }
            }
            else
            {
                invalid.Add("Zone");
            }

            // One issue is for one raft of its zone.
            if (request.RaftId is not { } raftId || !await RaftInZoneAsync(raftId, zone, stored?.RaftId, cancellationToken))
            {
                invalid.Add("Raft");
            }

            if (request.IssueTypeId is { } typeId)
            {
                type = await types.GetForEditAsync(typeId, cancellationToken);
                if (type is null || (!type.IsActive && type.Id != stored?.IssueTypeId))
                {
                    invalid.Add("Issue type");
                    type = null;
                }
            }

            if (request.ReceiverEmployeeId is { } receiverId && zone is not null)
            {
                // The receiver works in the zone; one saved earlier who has left since may stay on the issue.
                receiverName = (await issues.ReceiversAsync(zone.Id, cancellationToken)).FirstOrDefault(r => r.Id == receiverId)?.FullName;
                if (receiverName is null && receiverId == stored?.ReceiverEmployeeId)
                {
                    receiverName = stored.Receiver?.FullName;
                }
                else if (receiverName is null)
                {
                    invalid.Add("Receiver");
                }
            }
        }
        else if (request.ZoneId is not null || request.RaftId is not null || request.IssueTypeId is not null || request.ReceiverEmployeeId is not null)
        {
            invalid.Add("Disposal fields");
        }

        var inputs = request.Lines ?? [];
        if (inputs.Count > MaxLines || inputs.Any(l => l is null))
        {
            invalid.Add("Lines");
            return (null, AppError.InvalidFields(invalid.Distinct().ToList()));
        }

        var storedItems = stored?.Lines.Select(l => l.ItemId).ToHashSet() ?? [];
        var found = (await items.GetByIdsAsync(inputs.Select(l => l.ItemId).OfType<Guid>().Distinct().ToList(), cancellationToken)).ToDictionary(i => i.Id);
        var lines = new List<PreparedLine>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var field = $"Line {i + 1}";
            if (input.ItemId is not { } itemId || !found.TryGetValue(itemId, out var item) || item.IsHidden || (item.StatusId != StatusIds.Usage.InUse && !storedItems.Contains(itemId)))
            {
                invalid.Add($"{field} item");
                continue;
            }

            var unit = ReceiptLineUnits.Resolve(item, input.ItemUnitId);
            if (input.ItemUnitId is not null && unit is null)
            {
                invalid.Add($"{field} unit");
            }

            if (input.Quantity is not { } quantity || quantity <= 0 || quantity > PurchaseReceiptValidator.MaxQuantity || !PurchaseReceiptValidator.HasAllowedDecimals(quantity))
            {
                invalid.Add($"{field} quantity");
                continue;
            }

            if (input.SeedLotLineId is { } lotId && (item.Group != InputItemGroup.Seed || !await issues.IsLotOfItemAsync(item.Id, lotId, cancellationToken)))
            {
                invalid.Add($"{field} seed lot");
            }

            var note = input.Note?.Trim();
            if (note?.Length > MaxNoteLength)
            {
                invalid.Add($"{field} note");
            }

            lines.Add(new PreparedLine(item, unit, quantity, input.SeedLotLineId, string.IsNullOrEmpty(note) ? null : note));
        }

        if (invalid.Count > 0)
        {
            return (null, AppError.InvalidFields(invalid.Distinct().ToList()));
        }

        if (request.Complete || stored is { StatusId: StatusIds.InputIssue.Completed })
        {
            var missing = new List<string>();
            if (lines.Count == 0)
            {
                missing.Add("Lines");
            }

            if (kind == InputIssueKind.Disposal && string.IsNullOrEmpty(reason))
            {
                missing.Add("Reason");
            }

            if (kind == InputIssueKind.Use && type is null)
            {
                missing.Add("Issue type");
            }

            if (missing.Count > 0)
            {
                return (null, AppError.InvalidFields(missing));
            }
        }

        raft = request.RaftId is { } headerRaft ? (await rafts.GetByIdsAsync([headerRaft], cancellationToken)).FirstOrDefault() : null;
        var party = kind == InputIssueKind.Use ? JoinParty(zone?.Code, raft?.Code, receiverName) : reason ?? string.Empty;
        return (new Prepared(kind, issuedAt, zone, raft, type, request.ReceiverEmployeeId, receiverName, string.IsNullOrEmpty(reason) ? null : reason, string.IsNullOrEmpty(notes) ? null : notes, lines, party), null);
    }

    private async Task<bool> RaftInZoneAsync(Guid raftId, FarmingZone? zone, Guid? storedRaftId, CancellationToken cancellationToken)
    {
        var raft = (await rafts.GetByIdsAsync([raftId], cancellationToken)).FirstOrDefault();
        return raft is not null && (zone is null || raft.ZoneId == zone.Id) && (raft.StatusId == StatusIds.Usage.InUse || raft.Id == storedRaftId);
    }

    private static string JoinParty(string? zoneCode, string? raftCode, string? receiverName)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(zoneCode))
        {
            parts.Add(zoneCode);
        }

        if (!string.IsNullOrEmpty(raftCode))
        {
            parts.Add(raftCode);
        }

        if (!string.IsNullOrEmpty(receiverName))
        {
            parts.Add(receiverName);
        }

        var party = string.Join(" · ", parts);
        return party.Length <= 200 ? party : party[..200];
    }

    private static string PartyOf(InputIssue issue)
    {
        if (issue.Kind == InputIssueKind.Disposal)
        {
            return issue.Reason ?? string.Empty;
        }

        return JoinParty(issue.Zone?.Code, issue.Raft?.Code, issue.Receiver?.FullName);
    }

    // ---- Applying a save ----

    /// <summary>The save of an existing issue, after the checks of who may and which version it read.</summary>
    private async Task<Result<InputIssueView>> ReplaceAsync(InputIssue issue, SaveInputIssueRequest request, CancellationToken cancellationToken)
    {
        var (prepared, error) = await PrepareAsync(request, issue.Kind, issue, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (code, codeError) = await codes.ForEditAsync(request.Code, issue.Id, issue.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var wasCompleted = issue.StatusId == StatusIds.InputIssue.Completed;
        var before = Snapshot(issue);
        var ledgerBefore = LedgerKey(issue.Code, issue.IssuedAt, PartyOf(issue), issue.Lines.Select(l => (l.ItemId, l.SeedLotLineId, l.Quantity)));
        var linesBefore = LineKey(issue.Lines.OrderBy(l => l.LineNumber).Select(l => (l.ItemId, l.ItemUnitId, l.EnteredQuantity, l.SeedLotLineId, l.Note)));
        var newLines = LineKey(prepared!.Lines.Select(l => (l.Item.Id, l.Unit?.Id, l.EnteredQuantity, l.LotId, l.Note)));
        issue.Code = code!;
        // The lines of a Completed issue keep their cost snapshot unless they change.
        Apply(issue, prepared, replaceLines: !wasCompleted || linesBefore != newLines);
        unitOfWork.MarkChanged(issue);
        var ledgerAfter = LedgerKey(issue.Code, issue.IssuedAt, prepared.Party, issue.Lines.Select(l => (l.ItemId, l.SeedLotLineId, l.Quantity)));
        if (wasCompleted)
        {
            if (ledgerAfter != ledgerBefore)
            {
                await ledger.ReverseAsync(issue.Id, afterReceiptRemoved: false, cancellationToken);
                await PostAsync(issue, prepared.Party, cancellationToken);
                if (await ledger.CheckNotNegativeAsync(cancellationToken) is { } refused)
                {
                    return refused;
                }
            }
        }
        else if (request.Complete && await CompleteAsync(issue, prepared, cancellationToken) is { } completeError)
        {
            return completeError;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(issue));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry(wasCompleted ? "InputIssue.Edited" : "InputIssue.Updated", RecordType)
            {
                RecordId = issue.Id, RecordCode = issue.Code, Before = changedBefore, After = changedAfter,
            });
        }

        if (await SaveAsync(issue, cancellationToken) is { } saveError)
        {
            return saveError;
        }

        return await ViewAsync(await issues.GetForEditAsync(issue.Id, cancellationToken) ?? issue, cancellationToken);
    }

    /// <summary>Draft → Completed: the status, the quantities on the ledger and the check for a negative stock, all before the one save.</summary>
    private async Task<AppError?> CompleteAsync(InputIssue issue, Prepared prepared, CancellationToken cancellationToken)
    {
        var from = issue.StatusId;
        issue.StatusId = StatusIds.InputIssue.Completed;
        issue.CompletedAt = timeProvider.GetUtcNow();
        audit.RecordStatusChange(RecordType, issue.Id, issue.Code, from, StatusIds.InputIssue.Completed);
        await PostAsync(issue, prepared.Party, cancellationToken);
        return await ledger.CheckNotNegativeAsync(cancellationToken);
    }

    private Task PostAsync(InputIssue issue, string party, CancellationToken cancellationToken) =>
        ledger.PostIssueAsync(
            issue.Kind == InputIssueKind.Use ? InputStockMovementKind.Issue : InputStockMovementKind.Disposal, issue.Id, issue.Code, issue.IssuedAt, party,
            issue.Lines.OrderBy(l => l.LineNumber).Select(l => new StockPosting(l.ItemId, l.SeedLotLineId, -l.Quantity, null)).ToList(), cancellationToken);

    private void Apply(InputIssue issue, Prepared prepared, bool replaceLines)
    {
        issue.IssuedAt = prepared.IssuedAt;
        issue.ZoneId = prepared.Zone?.Id;
        issue.RaftId = prepared.Raft?.Id;
        issue.IssueTypeId = prepared.Type?.Id;
        issue.ReceiverEmployeeId = prepared.ReceiverId;
        issue.Reason = prepared.Reason;
        issue.Notes = prepared.Notes;
        if (!replaceLines)
        {
            if (issue.Lines.Count == 0)
            {
                AddLines(issue, prepared);
            }

            return;
        }

        foreach (var line in issue.Lines.ToList())
        {
            issue.Lines.Remove(line);
            issues.RemoveLine(line);
        }

        AddLines(issue, prepared);
    }

    private void AddLines(InputIssue issue, Prepared prepared)
    {
        var number = 0;
        foreach (var input in prepared.Lines)
        {
            var line = new InputIssueLine { IssueId = issue.Id, LineNumber = ++number, ItemId = input.Item.Id, SeedLotLineId = input.LotId, Note = input.Note };
            SetLine(line, input.Item, input.Unit, input.EnteredQuantity, input.Unit?.Factor ?? 1m);
            issue.Lines.Add(line);
            issues.AddLine(line);
        }
    }

    /// <summary>The unit, the base quantity and the cost snapshot of a line: the cost of the item per base unit × the factor, and the value half up to the dong.</summary>
    private static void SetLine(InputIssueLine line, FarmingInputItem item, InputItemUnit? unit, decimal enteredQuantity, decimal factor)
    {
        line.ItemUnitId = unit?.Id;
        line.UnitFactor = factor;
        line.Unit = unit?.Unit?.Name ?? item.Unit;
        line.EnteredQuantity = enteredQuantity;
        line.Quantity = enteredQuantity * factor;
        line.UnitCost = decimal.Round((item.CostPrice ?? 0m) * factor, 4);
        line.Value = PurchaseMoney.RoundHalfUp(enteredQuantity * line.UnitCost);
    }

    private static string LedgerKey(string code, DateTimeOffset issuedAt, string party, IEnumerable<(Guid ItemId, Guid? LotId, decimal Quantity)> lines) =>
        $"{code}@{issuedAt:O}@{party}@" + string.Join(";", lines.OrderBy(l => l.ItemId).ThenBy(l => l.LotId).ThenBy(l => l.Quantity).Select(l => $"{l.ItemId}|{l.LotId}|{l.Quantity}"));

    private static string LineKey(IEnumerable<(Guid ItemId, Guid? UnitId, decimal Quantity, Guid? LotId, string? Note)> lines) =>
        string.Join(";", lines.Select(l => $"{l.ItemId}|{l.UnitId}|{l.Quantity}|{l.LotId}|{l.Note}"));

    private static SaveInputIssueRequest ToRequest(InputIssue issue) => new(
        issue.Kind, issue.Code, issue.IssuedAt, issue.ZoneId, issue.RaftId, issue.IssueTypeId, issue.ReceiverEmployeeId, issue.Reason, issue.Notes,
        issue.Lines.OrderBy(l => l.LineNumber).Select(l => new InputIssueLineInput(l.ItemId, l.ItemUnitId, l.EnteredQuantity, l.SeedLotLineId, l.Note)).ToList());

    /// <summary>The second of two parallel saves with one code gets the same MSG-GEN-02 as the pre-check.</summary>
    private async Task<AppError?> SaveAsync(InputIssue issue, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            return await codes.DuplicateAsync(issue.Code, issue.Id, cancellationToken) ?? AppError.Conflict(UserMessages.AlreadyExists(ObjectName));
        }
    }

    private static object Snapshot(InputIssue i) => new
    {
        i.Code, i.Kind, i.IssuedAt, i.ZoneId, i.RaftId, i.IssueTypeId, i.ReceiverEmployeeId, i.Reason, i.Notes,
        Lines = i.Lines.OrderBy(l => l.LineNumber)
            .Select(l => new { l.LineNumber, l.ItemId, l.ItemUnitId, Quantity = l.EnteredQuantity, l.SeedLotLineId, l.UnitCost, l.Value, l.Note }).ToList(),
    };

    private AppError WrongStatus(InputIssue issue) =>
        AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {issue.Code}", statusCatalog.DisplayNameOf(issue.StatusId)));

    // ---- Views and filters ----

    private async Task<Result<InputIssueFilter>> FilterAsync(InputIssueListQuery query, PermissionAction action, CancellationToken cancellationToken)
    {
        var scope = await access.ScopeAsync(action, cancellationToken);
        if (scope.IsEmpty)
        {
            return AppError.Forbidden;
        }

        InputIssueSort? sort = query.SortBy switch
        {
            null or "code" => InputIssueSort.Code,
            "issuedAt" => InputIssueSort.IssuedAt,
            "totalValue" => InputIssueSort.TotalValue,
            "status" => InputIssueSort.Status,
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

        if (!Enum.IsDefined(query.Status) || (query.Kind is { } kind && !Enum.IsDefined(kind)))
        {
            return AppError.Invalid("Status");
        }

        IReadOnlyList<int>? statusIds = query.Status switch
        {
            InputIssueStatusFilter.Draft => [StatusIds.InputIssue.Draft],
            InputIssueStatusFilter.Completed => [StatusIds.InputIssue.Completed],
            InputIssueStatusFilter.Cancelled => [StatusIds.InputIssue.Cancelled],
            _ => null,
        };
        var text = DisplayNames.Normalize(query.Search);
        return new InputIssueFilter(
            text.Length == 0 ? null : text, query.Kind, statusIds,
            query.From is { } from ? BusinessClock.FromLocal(from.ToDateTime(TimeOnly.MinValue)) : null,
            query.To is { } to ? BusinessClock.FromLocal(to.AddDays(1).ToDateTime(TimeOnly.MinValue)) : null,
            query.ZoneId, query.RaftId, query.IssueTypeId, query.CreatedById, query.ReceiverId,
            scope.IncludeUse, scope.UseZoneIds, scope.IncludeDisposals, sort.Value, query.SortDir != "asc");
    }

    private async Task<IReadOnlyList<InputIssueListItem>> ListItemsAsync(IReadOnlyList<InputIssueRow> rows, CancellationToken cancellationToken)
    {
        var creators = rows.Select(r => r.Issue.CreatedBy).OfType<Guid>().Distinct().ToList();
        var names = creators.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(creators, cancellationToken);
        return rows.Select(r =>
        {
            var i = r.Issue;
            return new InputIssueListItem(
                i.Id, i.Kind, i.Code, i.StatusId, statusCatalog.DisplayNameOf(i.StatusId), i.IssuedAt, i.ZoneId, i.Zone?.Code, i.RaftId, i.Raft?.Code, i.IssueTypeId, i.IssueType?.Name,
                i.ReceiverEmployeeId, i.Receiver?.FullName, i.Reason, i.Notes, r.TotalValue, i.CreatedAt, i.CreatedBy is { } id ? names.GetValueOrDefault(id) : null, RowVersionGuard.Encode(i.RowVersion));
        }).ToList();
    }

    private async Task<InputIssueView> ViewAsync(InputIssue i, CancellationToken cancellationToken)
    {
        var lotIds = i.Lines.Select(l => l.SeedLotLineId).OfType<Guid>().Distinct().ToList();
        var lotCodes = await stock.LotCodesAsync(lotIds, cancellationToken);
        var creator = i.CreatedBy is { } createdBy ? (await accounts.GetDisplayNamesAsync([createdBy], cancellationToken)).GetValueOrDefault(createdBy) : null;
        // A line or a header that was just saved names a raft or a zone the entity may not have loaded: only what is loaded is shown.
        var lines = i.Lines.OrderBy(l => l.LineNumber).Select(l => new InputIssueLineView(
            l.Id, l.LineNumber, l.ItemId, l.Item?.Code ?? string.Empty, l.Item?.Name ?? string.Empty, l.Item?.Group ?? default, l.ItemUnitId, l.Unit, l.UnitFactor, l.EnteredQuantity, l.Quantity,
            l.Item?.Unit ?? l.Unit, l.SeedLotLineId, l.SeedLotLineId is { } lot ? lotCodes.GetValueOrDefault(lot) : null, l.UnitCost, l.Value, l.Note)).ToList();
        return new InputIssueView(
            i.Id, i.Kind, i.Code, i.StatusId, statusCatalog.DisplayNameOf(i.StatusId), i.IssuedAt, i.ZoneId, i.Zone?.Code, i.RaftId, i.Raft?.Code, i.IssueTypeId, i.IssueType?.Name,
            i.ReceiverEmployeeId, i.Receiver?.FullName, i.Reason, i.Notes, lines, lines.Sum(l => l.Value), i.CreatedAt, creator, i.CompletedAt, i.CancelledAt, i.CancelReason,
            RowVersionGuard.Encode(i.RowVersion));
    }
}
