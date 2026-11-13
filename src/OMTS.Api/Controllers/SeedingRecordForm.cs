using System.Globalization;
using OMTS.Api.Common;
using OMTS.Application.Features.Cultivation.Seeding;

namespace OMTS.Api.Controllers;

/// <summary>
/// Multipart fields of a seeding session (CYCLE-02, owner decision of 12/11/2026: 1 to 3 on-site photos beside it). Numbers are read with a dot, the date as <c>yyyy-MM-dd</c>; a value that cannot be read becomes
/// one the service refuses by name (a number: none; a date: the smallest date), never a silent default. <c>lanePercents</c> is one JSON object, lane number to percent.
/// </summary>
public sealed class SeedingRecordForm
{
    public string? SeedingDate { get; set; }

    public string? SeedLotId { get; set; }

    /// <summary>One field per lane.</summary>
    public string?[]? LaneNumbers { get; set; }

    public string? Quantity { get; set; }

    /// <summary>A JSON object, e.g. <c>{"1":60,"2":100}</c>; a lane left out takes 100 when recording and keeps its share when correcting.</summary>
    public string? LanePercents { get; set; }

    public bool ConfirmOverPlan { get; set; }

    public string? OverPlanReason { get; set; }

    /// <summary>When correcting.</summary>
    public string? Reason { get; set; }

    /// <summary>When correcting: the version of the session. An empty value stays empty (not null), as it did in the JSON body this form replaces: the check then answers on the version itself.</summary>
    [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? RowVersion { get; set; }

    /// <summary>When correcting: one field per photo to keep (its id); a photo left out is removed. Sending this or <c>photos</c> replaces the photos.</summary>
    public string?[]? KeepPhotoIds { get; set; }

    /// <summary>One part per photo, 1 to 3: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }

    public RecordSeedingRequest ToRecord(bool otherFilePart)
    {
        var (numbers, quantity, lot, percents, unreadable) = Read();
        return new RecordSeedingRequest(Date(SeedingDate), lot, numbers, quantity, ConfirmOverPlan, OverPlanReason, percents, unreadable, otherFilePart);
    }

    public CorrectSeedingRequest ToCorrect(bool otherFilePart)
    {
        var (numbers, quantity, lot, percents, unreadable) = Read();
        var keep = KeepPhotoIds?.Select(k => Guid.TryParse(k, out var id) ? id : Guid.Empty).ToList();
        return new CorrectSeedingRequest(
            Date(SeedingDate), lot, numbers, quantity, Reason, RowVersion, ConfirmOverPlan, OverPlanReason, percents, unreadable, otherFilePart,
            keep is not null || Photos is { Count: > 0 }, keep);
    }

    private (List<int>? Lanes, decimal? Quantity, Guid? Lot, Dictionary<int, decimal>? Percents, bool Unreadable) Read()
    {
        FormNumbers.TryDecimal(Quantity, out var quantity);
        return (
            FormNumbers.Numbers(LaneNumbers), quantity, Guid.TryParse(SeedLotId, out var lot) && lot != Guid.Empty ? lot : null,
            FormNumbers.LanePercents(LanePercents, out var unreadable), unreadable);
    }

    /// <summary>A blank date is none; one that cannot be read is the smallest date, which no check accepts.</summary>
    private static DateOnly? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateOnly.MinValue;
    }
}
