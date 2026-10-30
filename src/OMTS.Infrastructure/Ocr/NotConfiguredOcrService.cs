using OMTS.Application.Common.Models.Ocr;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// Default OCR adapter when no real provider is configured: reads nothing and reports a failure, so the
/// recording flow keeps the Draft, answers MSG04 and the user enters the values by hand.
/// </summary>
public sealed class NotConfiguredOcrService : IOcrService
{
    public const string ErrorCode = "NOT_CONFIGURED";

    public Task<OcrResult> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(OcrResult.Failed(ErrorCode));
}
