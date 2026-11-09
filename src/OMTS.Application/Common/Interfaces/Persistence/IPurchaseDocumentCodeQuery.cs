namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The purchasing documents whose code a user may type; each kind has codes of its own.</summary>
public enum PurchaseDocumentKind
{
    Receipt,
    Return,
    Payment,
}

/// <summary>Finds a stored document by code, every status included (Draft and Cancelled too).</summary>
public interface IPurchaseDocumentCodeQuery
{
    /// <summary>
    /// The id of the document of <paramref name="kind"/> whose code is <paramref name="code"/> regardless of letter case, other
    /// than <paramref name="exceptId"/>; null when there is none.
    /// </summary>
    Task<Guid?> FindAsync(PurchaseDocumentKind kind, string code, Guid? exceptId, CancellationToken cancellationToken);
}
