using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>The first OCR draft, kept unchanged for comparison with the confirmed values.</summary>
public sealed record OcrDraftValues(decimal? Ph, decimal? Salinity, DateTimeOffset? MeasuredAt);
