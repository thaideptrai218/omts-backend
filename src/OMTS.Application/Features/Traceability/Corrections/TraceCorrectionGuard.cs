using System.Text.Json;
using System.Text.Json.Nodes;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Corrections;

/// <summary>
/// Who may correct traceability data and when (TRACE-03, BR-TRACE-04): a Zone Manager of the raft's zone and the Cooperative Owner (§I.4.4: PROVENANCE, not the Accountant), on any record
/// of the correctable set whoever recorded it. It never relaxes <see cref="Cultivation.Cycles.CycleWriteGuard"/>: the cultivation screens stay locked after the close, and TRACE-03 has
/// its own rules here. A raft or zone that is Discontinued keeps its records read-only (GBR-01).
/// </summary>
public sealed class TraceCorrectionGuard(ZoneAccessGuard zoneAccess, RaftViews raftViews)
{
    /// <summary>{action} of MSG-GEN-04, the Owner's confirmation.</summary>
    public const string ConfirmAction = "correct traceability data";

    public Task<bool> MayCorrectAsync(Guid zoneId, CancellationToken cancellationToken) =>
        zoneAccess.AllowsAsync(PermissionFunctions.Provenance, PermissionAction.Edit, zoneId, cancellationToken);

    public Task<bool> IsOwnerAsync(CancellationToken cancellationToken) => zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    /// <summary>MSG-GEN-09 when the raft or its zone is read-only. The raft comes with its zone.</summary>
    public AppError? RaftRefusal(Raft raft) => raftViews.EditRefusal(raft);

    /// <summary>
    /// TRACE-03.1 and TRACE-03.2: the logs and the harvest data of a Closed cycle only; while the cycle is Open they are corrected in CYCLE-04.1 and HARVEST-01.1 (MSG-GEN-09).
    /// </summary>
    /// <param name="raft">The raft of the cycle, with its zone: a lot read without tracking brings it apart from its cycle.</param>
    public AppError? ClosedCycleRefusal(CultivationCycle cycle, Raft raft) => cycle.StatusId != StatusIds.CultivationCycle.Closed
        ? AppError.Conflict(UserMessages.WrongStatus($"The cultivation cycle of raft {raft.Code}", "Open"))
        : RaftRefusal(raft);
}

/// <summary>The audit actions of TRACE-03 (GBR-02); TRACE-02 reads its correction marks from them.</summary>
public static class TraceCorrectionAudit
{
    public const string SeedingRecordCorrected = "SeedingRecord.TraceCorrected";
    public const string CultivationLogCorrected = "CultivationLog.TraceCorrected";
    public const string HarvestLotCorrected = "HarvestLot.TraceCorrected";

    /// <summary>
    /// The changed values after the correction, with the Published lots it changed (BR-TRACE-05: there are no publication versions, so this row is what "published again" leaves) and
    /// whether a Zone Manager confirmed the Owner's agreement (MSG-TRACE-04). The Owner only confirms.
    /// </summary>
    public static JsonObject After(JsonObject changedAfter, IReadOnlyList<string> publishedLotCodes, bool ownerApprovalConfirmed)
    {
        changedAfter["AffectedPublishedLotCodes"] = new JsonArray(publishedLotCodes.Select(code => (JsonNode?)JsonValue.Create(code)).ToArray());
        changedAfter["OwnerApprovalConfirmed"] = ownerApprovalConfirmed;
        return changedAfter;
    }
}

/// <summary>The answers TRACE-03 shares between its three kinds of record.</summary>
public static class TraceCorrectionAnswers
{
    /// <summary>TRACE-03 step 6: MSG-TRACE-04 for a Zone Manager, MSG-GEN-04 for the Owner.</summary>
    public static UserMessage Confirmation(bool isOwner, IReadOnlyList<string> lotCodes) =>
        isOwner ? UserMessages.ConfirmAction(TraceCorrectionGuard.ConfirmAction) : UserMessages.TraceCorrectionAgreement(lotCodes);

    /// <summary>MSG-TRACE-04 (409): nothing is saved until the same request comes back with <c>ownerApproved: true</c> (TRACE-03.0.E4).</summary>
    public static AppError AgreementRequired(IReadOnlyList<HarvestLot> affected) => AppError.Conflict(
        UserMessages.TraceCorrectionAgreement(Codes(affected)),
        new Dictionary<string, object?>
        {
            ["confirmFlag"] = "ownerApproved",
            ["lotCodes"] = Codes(affected),
            ["publishedLotCodes"] = PublishedCodes(affected),
        });

    public static IReadOnlyList<string> Codes(IEnumerable<HarvestLot> lots) => lots.Select(l => l.LotCode).ToList();

    public static IReadOnlyList<string> PublishedCodes(IEnumerable<HarvestLot> lots) =>
        lots.Where(l => l.OriginState == HarvestLotOriginState.Published).Select(l => l.LotCode).ToList();

    /// <summary>The preview of steps 5 and 6.</summary>
    /// <param name="changedAfter">The changed values after, as the audit row would keep them; null when nothing changes.</param>
    public static TraceCorrectionPreview Preview(
        string recordType, Guid recordId, object before, object? after, JsonObject? changedAfter, IReadOnlyList<HarvestLot> affected, bool isOwner, string rowVersion)
    {
        var confirm = Confirmation(isOwner, Codes(affected));
        return new TraceCorrectionPreview(
            recordType,
            recordId,
            before,
            after,
            changedAfter?.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Key)).ToList() ?? [],
            affected.Select(l => new TraceAffectedLot(l.Id, l.LotCode, l.OriginState)).ToList(),
            PublishedCodes(affected),
            !isOwner,
            confirm.Text,
            confirm.Code,
            rowVersion);
    }
}
