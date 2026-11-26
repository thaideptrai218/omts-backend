using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;

namespace OMTS.Application.Features.Scans;

/// <summary>202 body of every asynchronous scan.</summary>
public sealed record ScanStarted(Guid ScanId);

/// <summary>Poll body of every asynchronous scan (API contract 26/11/2026, section 2).</summary>
/// <param name="Status">Pending, Done or Failed.</param>
/// <param name="ErrorCode">UNAVAILABLE, TIMEOUT, BAD_RESPONSE or NOT_CONFIGURED when Failed.</param>
/// <param name="Reading">Set only when Done.</param>
public sealed record ScanStatusView<TReading>(string Status, string? ErrorCode, TReading? Reading) where TReading : class;

/// <summary>A checked photo copied out of the request, so the background job does not depend on it.</summary>
public sealed record ScannedPhoto(byte[] Bytes, string MimeType);

/// <summary>
/// The start/poll mechanics every asynchronous photo scan shares: the photo is checked and copied, the job goes to the
/// <see cref="IScanQueue"/> for the caller's account, a full queue is OCR_BUSY (503), and a poll only finds the scans of the
/// caller in the given scope. The permission and the rules of what is scanned stay with each scan's own service.
/// </summary>
public sealed class PhotoScanGateway(IScanQueue queue, ICurrentUser currentUser)
{
    public const string PhotoField = "Photo";

    public Result<ScanStarted> Start(IncomingFile? photo, string scope, Func<IServiceProvider, ScannedPhoto, CancellationToken, Task<ScanOutcome>> work)
    {
        if (currentUser.AccountId is not { } accountId)
        {
            return AppError.Forbidden;
        }

        if (photo is null)
        {
            return AppError.Invalid(PhotoField);
        }

        var checkedPhoto = FileValidator.Validate([photo], FileRules.Photos);
        if (!checkedPhoto.IsValid)
        {
            return AppError.Invalid(PhotoField, checkedPhoto.Problem);
        }

        var file = checkedPhoto.Files[0];
        var scanned = new ScannedPhoto(ReadAll(file.File.Content), file.ContentType);
        var scanId = queue.TryEnqueue(accountId, scope, (services, cancellationToken) => work(services, scanned, cancellationToken));
        if (scanId is not { } id)
        {
            return new AppError(ErrorKind.ServiceUnavailable, UserMessages.OcrBusy);
        }

        return new ScanStarted(id);
    }

    public Result<ScanStatusView<TReading>> Get<TReading>(Guid scanId, string scope) where TReading : class
    {
        if (currentUser.AccountId is not { } accountId || queue.Find(scanId, accountId, scope) is not { } scan)
        {
            return AppError.NotFound;
        }

        return new ScanStatusView<TReading>(scan.Status.ToString(), scan.ErrorCode, scan.Reading as TReading);
    }

    private static byte[] ReadAll(Stream content)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return buffer.ToArray();
    }
}
