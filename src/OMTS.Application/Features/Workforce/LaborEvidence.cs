using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;

namespace OMTS.Application.Features.Workforce;

/// <summary>
/// Evidence images of attendance (LAB-04), piecework completion (LAB-05) and payments (LAB-09): JPEG/PNG, at most
/// 5 MB each and 5 per record, stored through <see cref="IFileStorage"/>; read URLs are short-lived.
/// </summary>
public sealed class LaborEvidence(IFileStorage storage)
{
    public const string Field = "Evidence";

    /// <summary>Checks and stores the files; the storage keys to append to the record.</summary>
    public async Task<Result<IReadOnlyList<string>>> UploadAsync(
        IReadOnlyList<IncomingFile> files, int alreadyStored, string category, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return AppError.Invalid(Field);
        }

        var rules = FileRules.LaborEvidence;
        if (alreadyStored + files.Count > rules.MaxFiles)
        {
            return AppError.Invalid(Field, $"At most {rules.MaxFiles} images per record ({alreadyStored} already saved).");
        }

        var checkedFiles = FileValidator.Validate(files, rules);
        if (!checkedFiles.IsValid)
        {
            return AppError.Invalid(Field, checkedFiles.Problem);
        }

        var keys = new List<string>();
        foreach (var file in checkedFiles.Files)
        {
            var uploaded = await storage.UploadAsync(file, category, cancellationToken);
            if (uploaded is not { Succeeded: true, Key: { } key })
            {
                foreach (var stored in keys)
                {
                    await storage.DeleteAsync(stored, cancellationToken);
                }

                return new AppError(ErrorKind.BadGateway, UserMessages.FileOperationFailed);
            }

            keys.Add(key);
        }

        return keys;
    }

    public async Task<IReadOnlyList<string>> ReadUrlsAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        foreach (var key in keys)
        {
            urls.Add(await storage.GetReadUrlAsync(key, cancellationToken: cancellationToken));
        }

        return urls;
    }
}
