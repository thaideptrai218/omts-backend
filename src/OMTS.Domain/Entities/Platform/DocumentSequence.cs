namespace OMTS.Domain.Entities.Platform;

/// <summary>
/// The last number given for a kind of document (<see cref="Key"/>, e.g. PN for purchase receipts; later HD sales invoices,
/// supplier payments and wage vouchers). A number is never given twice, also after its document was removed (a Draft);
/// gaps are allowed (a save that failed after taking its number).
/// </summary>
public class DocumentSequence
{
    public string Key { get; set; } = string.Empty;
    public long LastValue { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
