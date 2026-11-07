using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// Default OCR adapter when no real provider is configured: reads nothing and reports a failure, so the
/// recognition still answers the photo key with MSG-WATER-01, saves nothing, and the user enters the values by hand.
/// </summary>
public sealed class NotConfiguredOcrService : IOcrService
{
    public const string ErrorCode = "NOT_CONFIGURED";

    public Task<OcrResult> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(OcrResult.Failed(ErrorCode));
}
