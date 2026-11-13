using System.Globalization;
using OMTS.Api.Common;
using OMTS.Application.Features.Cultivation.Cycles;

namespace OMTS.Api.Controllers;

/// <summary>
/// Multipart fields of the plan of a cycle (CYCLE-01.0, owner decision of 12/11/2026: 1 to 3 photos beside it) and of a change of its photos. Numbers are read with a dot, the date as <c>yyyy-MM-dd</c>; a value that cannot
/// be read becomes one the service refuses by name (a number: none; a date: the smallest date), never a silent default. A field sent for the expected harvest date is ignored (it is gone).
/// </summary>
public sealed class CyclePlanForm
{
    /// <summary>The raft, when not in the route (<c>POST /api/cultivation-cycles</c>).</summary>
    public string? RaftId { get; set; }

    /// <summary>The nursery batch the cycle draws its seed from (v2, 13/11/2026): required to start a cycle.</summary>
    public string? NurseryBatchId { get; set; }

    /// <summary>The product the cycle will yield and sell (13/11/2026): required to start a cycle.</summary>
    public string? ProductId { get; set; }

    public string? Note { get; set; }

    public string? StartDate { get; set; }

    public string? ExpectedSeedQuantity { get; set; }

    public string? TargetYieldKg { get; set; }

    /// <summary>When changing the photos: the version of the cycle. An empty value stays empty (not null): the check then answers on the version itself.</summary>
    [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? RowVersion { get; set; }

    /// <summary>When changing the photos: one field per photo to keep (its id); a photo left out is removed.</summary>
    public string?[]? KeepPhotoIds { get; set; }

    /// <summary>One part per photo, 1 to 3: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }

    /// <summary>The start of a cycle (v2): a blank raft in the form takes <paramref name="routeRaftId"/>; ids that cannot be read become none, which the service refuses by name.</summary>
    public CreateCycleRequest ToCreate(bool otherFilePart, Guid? routeRaftId = null) => new(
        routeRaftId ?? (Guid.TryParse(RaftId, out var raft) ? raft : null), Guid.TryParse(NurseryBatchId, out var batch) ? batch : null, Date(StartDate),
        Number(TargetYieldKg), Note, otherFilePart, Guid.TryParse(ProductId, out var product) ? product : null);

    /// <summary>The OLD start without a nursery batch: used only by the test seam; no public route reads it since v2.</summary>
    public StartCycleRequest ToStart(bool otherFilePart)
    {
        return new StartCycleRequest(Date(StartDate), Number(ExpectedSeedQuantity), Number(TargetYieldKg), otherFilePart);
    }

    public ChangePlanPhotosRequest ToPhotos(bool otherFilePart) =>
        new(RowVersion, KeepPhotoIds?.Select(k => Guid.TryParse(k, out var id) ? id : Guid.Empty).ToList(), otherFilePart);

    /// <summary>A blank number is none (an estimate left out); one that cannot be read is -1, which no estimate accepts, so it is refused by its name instead of being dropped.</summary>
    private static decimal? Number(string? value) => FormNumbers.TryDecimal(value, out var number) ? number : -1m;

    /// <summary>A blank date is none (the cycle starts today); one that cannot be read is the largest date, which no start date accepts (it is in the future), so it is refused by name.</summary>
    private static DateOnly? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateOnly.MaxValue;
    }
}
