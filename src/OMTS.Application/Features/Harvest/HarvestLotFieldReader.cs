using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>The checked fields of a lot; <paramref name="EstimatedKg"/> holds the worked-out value when none was sent.</summary>
internal sealed record HarvestLotFields(
    IReadOnlyList<Lane> Lanes, IReadOnlySet<Guid> FinishedLaneIds, decimal CageCount, decimal EstimatedKg, IReadOnlyDictionary<Guid, decimal?>? Shares = null);

/// <summary>What a correction adds to the fields of a harvest: the reason, kept in the audit log, and why the finished lanes cannot be so.</summary>
/// <param name="OwnLaneIds">
/// The lanes of the lot that stay allowed whatever their status. TRACE-03.2 passes the lot's own lanes: a lane Removed after the close keeps its records (BR-FARM-04), so only a lane
/// the correction adds must be In Use. HARVEST-01.1 passes none, as no lane of an Open cycle can be removed.
/// </param>
internal sealed record HarvestLotCorrectionInput(
    string? Reason, string? FinishedHint, IReadOnlySet<Guid>? OwnLaneIds = null, decimal? ExistingCageCount = null, decimal? ExistingEstimatedKg = null, IReadOnlySet<Guid>? ExistingLaneIds = null);

/// <summary>
/// HARVEST-01 step 6: at least one lane Growing or Being harvested, the finished lanes among them, cages and estimated kg above 0 (BR-HARVEST-01, BR-HARVEST-02), and when recording the
/// purchase request it harvests for (decision of 11/11/2026). Every invalid field is listed (MSG-GEN-01).
/// </summary>
internal static class HarvestLotFieldReader
{
    public const string PurchaseRequestField = "Purchase request";
    public const string LanesField = "Lanes";
    public const string FinishedLanesField = "Finished lanes";
    public const string CagesField = "Number of cages";
    public const string EstimatedKgField = "Estimated kg";
    public const string PhotosField = "Photos";
    public const string ReasonField = "Reason";
    public const int MaxReasonLength = 1000;

    /// <summary>Checks the fields against the lanes as they are now; a null answer means <paramref name="fields"/> can be saved.</summary>
    /// <param name="states">The lanes of the raft today; for a correction, as the lot being corrected sees them.</param>
    /// <param name="kgPerCage">What the estimated kg is worked out with when none was sent.</param>
    /// <param name="photoHint">Why the photos were refused, or null.</param>
    /// <param name="correction">The fields only a correction has.</param>
    public static AppError? Read(
        RecordHarvestRequest request, Raft raft, IReadOnlyDictionary<Guid, LaneState> states, decimal kgPerCage, string? photoHint, out HarvestLotFields? fields,
        HarvestLotCorrectionInput? correction = null)
    {
        fields = null;
        var check = new FieldCheck();
        check.Check(correction is not null || request.PurchaseRequestId is not null, PurchaseRequestField);
        var (lanes, laneHint) = Lanes(raft, request.LaneNumbers, states, correction?.OwnLaneIds, correction?.ExistingLaneIds);
        check.Check(lanes is not null && laneHint is null, LanesField);
        var finished = Finished(request, lanes);
        check.Check(finished is not null && correction?.FinishedHint is null, FinishedLanesField);
        var cagesAreValid = HarvestQuantityRules.CagesAreValid(request.CageCount);
        check.Check(cagesAreValid, CagesField);
        var worked = cagesAreValid ? HarvestQuantityRules.KgOf(request.CageCount!.Value, kgPerCage) : (decimal?)null;
        var estimated = request.EstimatedKg ?? worked;
        var estimatedIsValid = !request.EstimatedKgIsUnreadable
            && (estimated is null || (estimated > 0 && estimated <= PurchaseReceiptValidator.MaxQuantity && PurchaseReceiptValidator.HasAllowedDecimals(estimated.Value)));
        // A kg sent by hand stays near cages times the kg per cage; a correction that changes neither field is not judged again, so a lot saved before the rule can still be corrected.
        var unchanged = correction is not null && request.CageCount == correction.ExistingCageCount && estimated == correction.ExistingEstimatedKg;
        var kgIsSane = !estimatedIsValid || !cagesAreValid || unchanged || request.EstimatedKg is null
            || HarvestQuantityRules.KgIsWithinCeiling(request.EstimatedKg.Value, request.CageCount!.Value, kgPerCage);
        check.Check(estimatedIsValid && kgIsSane, EstimatedKgField);
        check.Check(photoHint is null, PhotosField);
        if (correction is not null)
        {
            check.Required(correction.Reason, ReasonField, MaxReasonLength);
        }

        if (!check.IsValid)
        {
            return check.Invalid switch
            {
                [LanesField] when laneHint is not null => AppError.Invalid(LanesField, laneHint),
                [FinishedLanesField] when correction?.FinishedHint is { } finishedHint => AppError.Invalid(FinishedLanesField, finishedHint),
                [PhotosField] => AppError.Invalid(PhotosField, photoHint),
                [CagesField] => AppError.Invalid(CagesField, $"Enter more than 0 and at most {HarvestQuantityRules.MaxCages:0} cages, with at most 2 decimals (0.5 is half a cage)."),
                [EstimatedKgField] when estimatedIsValid && !kgIsSane => AppError.Invalid(EstimatedKgField, HarvestQuantityRules.KgCeilingHint(request.CageCount!.Value, kgPerCage)),
                var invalid => AppError.InvalidFields(invalid),
            };
        }

        // The cycle target yield is a plan, not a cap (owner decision 12/11/2026): the harvest is never judged against it.
        fields = new HarvestLotFields(lanes!, finished!, request.CageCount!.Value, estimated!.Value);
        return null;
    }

    /// <summary>The photos are checked once, as their content does not change: the answer holds why they were refused. A new lot needs 1 to 3 on-site photos (owner decision of 12/11/2026).</summary>
    /// <param name="kept">How many current photos stay on the lot.</param>
    /// <param name="keysAreKnown">Every key the client asked to keep is a photo of this lot.</param>
    /// <param name="minimum">The least photos the lot must end with: 1 for a new lot or one that has photos; 0 for one recorded before the photos, which stays valid without.</param>
    public static (IReadOnlyList<ValidatedFile> Files, string? Hint) ReadPhotos(IReadOnlyList<IncomingFile> photos, bool otherFilePart, int kept = 0, bool keysAreKnown = true, int minimum = 0)
    {
        if (otherFilePart)
        {
            return ([], "Send each photo in a part named photos.");
        }

        if (!keysAreKnown)
        {
            return ([], "Keep only photos of this lot.");
        }

        if (kept + photos.Count < minimum)
        {
            return ([], $"Cần ít nhất {minimum} ảnh chụp tại hiện trường (tối đa {StepPhotoService.MaxPhotos} ảnh).");
        }

        if (kept + photos.Count > StepPhotoService.MaxPhotos)
        {
            return ([], $"Tối đa {StepPhotoService.MaxPhotos} ảnh cho mỗi lô thu hoạch.");
        }

        var validated = FileValidator.Validate(photos, FileRules.Photos with { MaxFiles = StepPhotoService.MaxPhotos });
        return validated.IsValid ? (validated.Files, null) : ([], validated.Problem);
    }

    /// <summary>
    /// One or more lanes of the raft, each In Use (or one of <paramref name="ownLaneIds"/>, whatever its status) and given once; a lane that is Not seeded or Harvested today is named in the hint.
    /// </summary>
    private static (IReadOnlyList<Lane>? Lanes, string? Hint) Lanes(
        Raft raft, IReadOnlyList<int>? numbers, IReadOnlyDictionary<Guid, LaneState> states, IReadOnlySet<Guid>? ownLaneIds, IReadOnlySet<Guid>? existingLaneIds = null)
    {
        if (numbers is null || numbers.Count == 0 || numbers.Distinct().Count() != numbers.Count)
        {
            return (null, null);
        }

        var lanes = raft.Lanes
            .Where(l => numbers.Contains(l.LaneNumber) && (l.StatusId == StatusIds.Lane.InUse || (ownLaneIds?.Contains(l.Id) ?? false)))
            .ToList();
        if (lanes.Count != numbers.Count)
        {
            return (null, null);
        }

        // A damaged lane or one out of service cannot be added to a harvest (decision of 12/11/2026); a lane the lot already holds stays, so the lot can still be corrected.
        var refused = lanes
            .Where(l => !LaneStateRules.CanBeHarvested(states[l.Id]) || ((l.IsDamaged || l.IsOutOfService) && !(existingLaneIds?.Contains(l.Id) ?? false) && !(ownLaneIds?.Contains(l.Id) ?? false)))
            .Select(l => l.LaneNumber).Order().ToList();
        return (lanes, refused.Count == 0 ? null : $"Lanes {string.Join(", ", refused)} cannot be harvested now.");
    }

    /// <summary>The ids of the finished lanes, each given once and among the lanes of the harvest; null when they are not.</summary>
    private static IReadOnlySet<Guid>? Finished(RecordHarvestRequest request, IReadOnlyList<Lane>? lanes)
    {
        var numbers = request.FinishedLaneNumbers;
        if (numbers is null || numbers.Distinct().Count() != numbers.Count)
        {
            return null;
        }

        var sent = request.LaneNumbers ?? [];
        return numbers.All(sent.Contains) ? (lanes ?? []).Where(l => numbers.Contains(l.LaneNumber)).Select(l => l.Id).ToHashSet() : null;
    }
}
