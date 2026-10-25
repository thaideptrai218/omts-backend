namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Storage keys the records still point to, for the orphan file clean-up.</summary>
public interface IStoredFileReferenceQuery
{
    /// <summary>
    /// Every key of the category's column (see <see cref="Features.Files.FileCategories"/>); a category without a
    /// known column throws, so nothing is ever cleaned up on a guess.
    /// </summary>
    Task<IReadOnlySet<string>> ReferencedKeysAsync(string category, CancellationToken cancellationToken);
}
