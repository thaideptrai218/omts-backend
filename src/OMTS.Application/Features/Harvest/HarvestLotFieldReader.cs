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
internal sealed record HarvestLotFields(IReadOnlyList<Lane> Lanes, IReadOnlySet<Guid> FinishedLaneIds, int CageCount, decimal EstimatedKg);

/// <summary>HARVEST-01 step 6: at least one lane Growing or Being harvested, the finished lanes among them, cages and estimated kg above 0 (BR-HARVEST-01, BR-HARVEST-02). Every invalid field is listed (MSG-GEN-01).</summary>
internal static class HarvestLotFieldReader
{
    public const string LanesField = "Lanes";
    public const string FinishedLanesField = "Finished lanes";
    public const string CagesField = "Number of cages";
    public const string EstimatedKgField = "Estimated kg";
    public const string PhotosField = "Photos";

    /// <summary>Checks the fields against the lanes as they are now; a null answer means <paramref name="fields"/> can be saved.</summary>
    /// <param name="states">The lanes of the raft today.</param>
    /// <param name="photoHint">Why the photos were refused, or null.</param>
    public static AppError? Read(
        RecordHarvestRequest request, Raft raft, IReadOnlyDictionary<Guid, LaneState> states, decimal kgPerCage, string? photoHint, out HarvestLotFields? fields)
    {
        fields = null;
        var check = new FieldCheck();
        var (lanes, laneHint) = Lanes(raft, request.LaneNumbers, states);
        check.Check(lanes is not null && laneHint is null, LanesField);
        var finished = Finished(request, lanes);
        check.Check(finished is not null, FinishedLanesField);
        check.Check(request.CageCount is > 0, CagesField);
        var estimated = request.EstimatedKg ?? (request.CageCount is > 0 ? request.CageCount * kgPerCage : null);
        var estimatedIsValid = !request.EstimatedKgIsUnreadable
            && (estimated is null || (estimated > 0 && estimated <= PurchaseReceiptValidator.MaxQuantity && PurchaseReceiptValidator.HasAllowedDecimals(estimated.Value)));
        check.Check(estimatedIsValid, EstimatedKgField);
        check.Check(photoHint is null, PhotosField);
        if (!check.IsValid)
        {
            return check.Invalid switch
            {
                [LanesField] when laneHint is not null => AppError.Invalid(LanesField, laneHint),
                [PhotosField] => AppError.Invalid(PhotosField, photoHint),
                var invalid => AppError.InvalidFields(invalid),
            };
        }

        fields = new HarvestLotFields(lanes!, finished!, request.CageCount!.Value, estimated!.Value);
        return null;
    }

    /// <summary>The photos are optional and checked once, as their content does not change: the answer holds why they were refused.</summary>
    public static (IReadOnlyList<ValidatedFile> Files, string? Hint) ReadPhotos(IReadOnlyList<IncomingFile> photos, bool otherFilePart)
    {
        if (otherFilePart)
        {
            return ([], "Send each photo in a part named photos.");
        }

        if (photos.Count > HarvestLot.MaxPhotos)
        {
            return ([], $"A lot holds at most {HarvestLot.MaxPhotos} photos.");
        }

        var validated = FileValidator.Validate(photos, FileRules.Photos);
        return validated.IsValid ? (validated.Files, null) : ([], validated.Problem);
    }

    /// <summary>One or more lanes of the raft, each In Use and given once; a lane that is Not seeded or Harvested today is named in the hint.</summary>
    private static (IReadOnlyList<Lane>? Lanes, string? Hint) Lanes(Raft raft, IReadOnlyList<int>? numbers, IReadOnlyDictionary<Guid, LaneState> states)
    {
        if (numbers is null || numbers.Count == 0 || numbers.Distinct().Count() != numbers.Count)
        {
            return (null, null);
        }

        var lanes = raft.Lanes.Where(l => numbers.Contains(l.LaneNumber) && l.StatusId == StatusIds.Lane.InUse).ToList();
        if (lanes.Count != numbers.Count)
        {
            return (null, null);
        }

        var refused = lanes.Where(l => !LaneStateRules.CanBeHarvested(states[l.Id])).Select(l => l.LaneNumber).Order().ToList();
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
