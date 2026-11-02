
namespace OMTS.Application.Features.Files.Models;

/// <summary>Result of <see cref="FileValidator.Validate"/>: the checked files, or the first invalid file's name.</summary>
public sealed record FileValidationResult(IReadOnlyList<ValidatedFile> Files, string? InvalidFileName, string? Problem)
{
    public bool IsValid => Problem is null;
}
