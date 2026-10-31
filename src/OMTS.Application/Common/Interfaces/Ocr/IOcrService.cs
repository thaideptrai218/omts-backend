using OMTS.Application.Common.Models.Ocr;

namespace OMTS.Application.Common.Interfaces.Ocr;

/// <summary>
/// OCR Service adapter (ENV-01): reads pH, salinity and the measurement time from a photo of the water
/// meter. Only the meter photo is sent. The result is only a draft until a person confirms it (NFR-05).
/// Provider failures are returned as a failed <see cref="OcrResult"/>, never thrown; the caller enforces
/// the timeout through the cancellation token.
/// </summary>
public interface IOcrService
{
    Task<OcrResult> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken);
}
