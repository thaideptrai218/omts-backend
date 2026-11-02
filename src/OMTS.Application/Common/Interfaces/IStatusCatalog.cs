namespace OMTS.Application.Common.Interfaces;

/// <summary>The fixed Status catalog (BR-29), readable without a database round trip.</summary>
public interface IStatusCatalog
{
    /// <summary>Code of a status id (e.g. 302 → "LOCKED"); throws for an unknown id (programming error).</summary>
    string CodeOf(int statusId);

    /// <summary>Display name of a status id (e.g. 302 → "Locked"), as used in MSG-GEN-09.</summary>
    string DisplayNameOf(int statusId);
}
