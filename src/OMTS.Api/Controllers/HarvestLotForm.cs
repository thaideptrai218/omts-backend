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

    /// <summary>The Zone Manager answers the over-plan warning (409, confirmFlag confirmOverPlan): send true with <see cref="OverPlanReason"/>.</summary>
    public bool ConfirmOverPlan { get; set; }

    public string? OverPlanReason { get; set; }

    /// <summary>Left out when recording: cages times the average kg per cage. Left out when correcting: it stays, or when the cages changed, cages times the kg per cage of the lot.</summary>
    public string? EstimatedKg { get; set; }

    /// <summary>One part per photo, none is fine: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }
}

/// <summary>Multipart fields of TRACE-03.2: those of a lot correction and the Zone Manager's answer to MSG-TRACE-04.</summary>
public sealed class TraceHarvestLotForm : HarvestLotForm
{
    /// <summary><c>true</c> when the Cooperative Owner agreed in person that the data is wrong; the Owner never sends it.</summary>
    public bool OwnerApproved { get; set; }
}
