namespace OMTS.Api.Controllers;

/// <summary>Multipart fields of a harvest (HARVEST-01). <see cref="KeepPhotoKeys"/>, <see cref="Reason"/> and <see cref="RowVersion"/> belong to a correction.</summary>
public sealed class HarvestLotForm
{
    /// <summary>One field per photo to keep; a photo left out is removed.</summary>
    public string?[]? KeepPhotoKeys { get; set; }

    public string? Reason { get; set; }

    public string? RowVersion { get; set; }

    /// <summary>One field per lane of the harvest.</summary>
    public string?[]? LaneNumbers { get; set; }

    /// <summary>One field per lane marked finished; none is fine.</summary>
    public string?[]? FinishedLaneNumbers { get; set; }

    public string? CageCount { get; set; }

    /// <summary>Left out when recording: cages times the average kg per cage. Left out when correcting: it stays, or when the cages changed, cages times the kg per cage of the lot.</summary>
    public string? EstimatedKg { get; set; }

    /// <summary>One part per photo, none is fine: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }
}
