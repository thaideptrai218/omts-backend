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

    public string? CageCount { get; set; }

    /// <summary>
    /// The share of each product on the lot, one entry per product: <c>productLines[0].productId</c>, <c>productLines[0].cageCount</c> and <c>productLines[0].kg</c> (indexes 0, 1, 2… with no gap). With lines,
    /// <c>cageCount</c> and <c>estimatedKg</c> of the form are ignored: the lot's cages and kg are the sums of its lines.
    /// </summary>
    public List<HarvestLotProductLineForm>? ProductLines { get; set; }

    /// <summary>Left out when recording: cages times the average kg per cage. Left out when correcting: it stays, or when the cages changed, cages times the kg per cage of the lot.</summary>
    public string? EstimatedKg { get; set; }

    /// <summary>One part per photo, none is fine: JPEG or PNG, at most 10 MB each.</summary>
    public List<IFormFile>? Photos { get; set; }
}

/// <summary>One product line of a harvest form: the product, its cages (above 0) and its kg (left out: the cages times the kg of a lồng of the product, or the kg per cage of the lot).</summary>
public sealed class HarvestLotProductLineForm
{
    public string? ProductId { get; set; }
    public string? CageCount { get; set; }
    public string? Kg { get; set; }
}

/// <summary>Multipart fields of TRACE-03.2: those of a lot correction and the Zone Manager's answer to MSG-TRACE-04.</summary>
public sealed class TraceHarvestLotForm : HarvestLotForm
{
    /// <summary><c>true</c> when the Cooperative Owner agreed in person that the data is wrong; the Owner never sends it.</summary>
    public bool OwnerApproved { get; set; }
}
