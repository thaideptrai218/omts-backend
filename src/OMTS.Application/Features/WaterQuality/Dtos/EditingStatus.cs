using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>Other people editing the record, with MSG58 for the first of them.</summary>
public sealed record EditingStatus(IReadOnlyList<RecordEditorView> OtherEditors, UserMessage? Notice);
