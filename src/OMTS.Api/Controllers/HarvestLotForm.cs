namespace OMTS.Api.Controllers;

/// <summary>Multipart fields of a harvest (HARVEST-01). <see cref="KeepPhotoKeys"/>, <see cref="Reason"/> and <see cref="RowVersion"/> belong to a correction.</summary>
public class HarvestLotForm
{
    /// <summary>One field per photo to keep; a photo left out is removed.</summary>
    public string?[]? KeepPhotoKeys { get; set; }

    public string? Reason { get; set; }

    public string? RowVersion { get; set; }

    /// <summary>One field per lane of the harvest.</summary>
    public string?[]? LaneNumbers { get; set; }

    /// <summary>One field per lane marked finished; none is fine.</summary>
    public string?[]? FinishedLaneNumbers { get; set; }

    /// <summary>Cages, a number with a dot and at most 2 decimals (0.5 is half a cage).</summary>
    public string? CageCount { get; set; }

    /// <summary>Accepted and ignored: the cycle target is a plan, not a cap (owner decision 12/11/2026), so a harvest above it is never asked about.</summary>
    public bool ConfirmOverPlan { get; set; }

    /// <summary>Accepted and ignored, like <see cref="ConfirmOverPlan"/>.</summary>
    public string? OverPlanReason { get; set; }

    /// <summary>When recording: the id of the purchase request the trip harvests for (decision of 11/11/2026). A correction does not read it.</summary>
    public string? PurchaseRequestId { get; set; }

    /// <summary>Left out when recording: cages times the average kg per cage. Left out when correcting: it stays, or when the cages changed, cages times the kg per cage of the lot.</summary>
    public string? EstimatedKg { get; set; }

    /// <summary>
    /// The employees who worked on the trip (decision of 12/11/2026): one field per employee id, none is fine. Not sent when correcting: the crew stays, unless <see cref="ReplaceCrew"/> is true.
    /// </summary>
    public string?[]? Crew { get; set; }

    /// <summary>Send true to make the crew of a correction exactly the <c>crew</c> fields, none meaning an empty crew: a form cannot send an empty list, so this is how a crew is cleared.</summary>
    public bool ReplaceCrew { get; set; }

    /// <summary>
    /// The share of each lane the lot harvested (owner decision of 12/11/2026): one JSON object, lane number to percent, e.g. <c>{"1":60,"2":100}</c>. A lane left out takes what is left when the lot finishes it,
    /// else no share is entered; a correction keeps the share of a lane it does not name.
    /// </summary>
    public string? LanePercents { get; set; }

    /// <summary>One part per photo, 1 to 3 when recording and for a lot that has photos: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }
}

/// <summary>Multipart fields of TRACE-03.2: those of a lot correction and the Zone Manager's answer to MSG-TRACE-04.</summary>
public sealed class TraceHarvestLotForm : HarvestLotForm
{
    /// <summary><c>true</c> when the Cooperative Owner agreed in person that the data is wrong; the Owner never sends it.</summary>
    public bool OwnerApproved { get; set; }
}
