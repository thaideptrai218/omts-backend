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

    /// <summary>When completing, Owner only: complete although a crew member with work that day is not checked out (MSG-LABOR-37).</summary>
    public bool OwnerApproved { get; set; }

    /// <summary>With <see cref="OwnerApproved"/>: why, 1 to 1000 characters.</summary>
    public string? ApprovalReason { get; set; }

    /// <summary>When editing: one field per start photo to keep (its id); a photo left out is removed. Sending this or <c>photos</c> replaces the photos.</summary>
    public string?[]? KeepPhotoIds { get; set; }

    /// <summary>The employees who worked on it (decision of 17/11/2026): one field per employee id, none is fine. Not sent when editing: the crew stays, unless <see cref="ReplaceCrew"/> is true.</summary>
    public string?[]? Crew { get; set; }

    /// <summary>Send true to make the crew exactly the <c>crew</c> fields, none meaning an empty crew: a form cannot send an empty list, so this is how a crew is cleared.</summary>
    public bool ReplaceCrew { get; set; }

    /// <summary>One part per photo, 1 to 3 when starting or completing: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }

    public StartNurseryRequest ToStart(bool otherFilePart)
    {
        FormNumbers.TryDecimal(Quantity, out var quantity);
        var crew = FormNumbers.Crew(Crew, ReplaceCrew, out var crewUnreadable);
        return new StartNurseryRequest(Guid.TryParse(SeedLotId, out var lot) ? lot : null, quantity, Date(StartDate), Note, otherFilePart, crew, crewUnreadable);
    }

    public EditNurseryRequest ToEdit(bool otherFilePart)
    {
        FormNumbers.TryDecimal(Quantity, out var quantity);
        var keep = KeepPhotoIds?.Select(k => Guid.TryParse(k, out var id) ? id : Guid.Empty).ToList();
        var crew = FormNumbers.Crew(Crew, ReplaceCrew, out var crewUnreadable);
        return new EditNurseryRequest(quantity, Date(StartDate), Note, RowVersion, keep is not null || Photos is { Count: > 0 }, keep, otherFilePart, crew, crewUnreadable);
    }

    public CompleteNurseryRequest ToComplete(bool otherFilePart)
    {
        FormNumbers.TryDecimal(Survivors, out var survivors);
        return new CompleteNurseryRequest(survivors, Date(CompletedOn), RowVersion, otherFilePart, OwnerApproved, ApprovalReason);
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
