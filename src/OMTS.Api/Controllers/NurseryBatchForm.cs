using System.Globalization;
using OMTS.Api.Common;
using OMTS.Application.Features.Cultivation.Nursery;

namespace OMTS.Api.Controllers;

/// <summary>
/// Multipart fields of the nursery (NURSERY-01 to NURSERY-03): the numbers are read with a dot, the dates as <c>yyyy-MM-dd</c>. A field sent for the shares of the survivors (before v2) is ignored.
/// A value that cannot be read becomes one the service refuses by name (a number: none; a date: the smallest date), never a silent default.
/// </summary>
public sealed class NurseryBatchForm
{
    public string? SeedLotId { get; set; }

    public string? Quantity { get; set; }

    public string? StartDate { get; set; }

    public string? Note { get; set; }

    public string? RowVersion { get; set; }

    /// <summary>When completing: the pieces that survived.</summary>
    public string? Survivors { get; set; }

    public string? CompletedOn { get; set; }

    /// <summary>When editing: one field per start photo to keep (its id); a photo left out is removed. Sending this or <c>photos</c> replaces the photos.</summary>
    public string?[]? KeepPhotoIds { get; set; }

    /// <summary>One part per photo, 1 to 3 when starting or completing: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }

    public StartNurseryRequest ToStart(bool otherFilePart)
    {
        FormNumbers.TryDecimal(Quantity, out var quantity);
        return new StartNurseryRequest(Guid.TryParse(SeedLotId, out var lot) ? lot : null, quantity, Date(StartDate), Note, otherFilePart);
    }

    public EditNurseryRequest ToEdit(bool otherFilePart)
    {
        FormNumbers.TryDecimal(Quantity, out var quantity);
        var keep = KeepPhotoIds?.Select(k => Guid.TryParse(k, out var id) ? id : Guid.Empty).ToList();
        return new EditNurseryRequest(quantity, Date(StartDate), Note, RowVersion, keep is not null || Photos is { Count: > 0 }, keep, otherFilePart);
    }

    public CompleteNurseryRequest ToComplete(bool otherFilePart)
    {
        FormNumbers.TryDecimal(Survivors, out var survivors);
        return new CompleteNurseryRequest(survivors, Date(CompletedOn), RowVersion, otherFilePart);
    }

    /// <summary>A blank date is none (the service takes today); one that cannot be read is the smallest date, which no check accepts.</summary>
    private static DateOnly? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateOnly.MinValue;
    }
}
