namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Gives document numbers from one sequence per key (<see cref="Constants.DocumentSequenceKeys"/>). Each call takes the next
/// number for good, outside the caller's unit of work: a number is never given twice, also under parallel calls or after
/// its document was removed, and a save that fails afterwards leaves a gap. Call it once the document is valid, just before
/// the one save that holds the document and its follow-up rows.
/// </summary>
public interface IDocumentNumberAllocator
{
    /// <summary>The next number of <paramref name="key"/>, 1 for a key that has none yet.</summary>
    Task<long> NextAsync(string key, CancellationToken cancellationToken);

    /// <summary>The number <see cref="NextAsync"/> would give now, without taking it (1 for a key that has none yet); only a proposal, as a parallel caller may take it first.</summary>
    Task<long> PeekAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException("This allocator cannot preview the next number.");
}

