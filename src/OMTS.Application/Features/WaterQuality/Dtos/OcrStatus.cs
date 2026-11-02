using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>How the OCR step went: Success (values read), Failed (error or timeout, enter manually), NotCalled (already read or a re-send).</summary>
public enum OcrStatus
{
    Success,
    Failed,
    NotCalled,
}
