namespace OMTS.Api.Controllers;

/// <summary>Multipart fields of a recorded harvest (HARVEST-01).</summary>
public sealed class HarvestLotForm
{
    /// <summary>One field per lane of the harvest.</summary>
    public string?[]? LaneNumbers { get; set; }

    /// <summary>One field per lane marked finished; none is fine.</summary>
    public string?[]? FinishedLaneNumbers { get; set; }

    public string? CageCount { get; set; }

    /// <summary>Left out: cages times the average kg per cage.</summary>
    public string? EstimatedKg { get; set; }

    /// <summary>One part per photo, none is fine: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }
}
