using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>Someone with the edit form open (NFR-13); the record is never locked.</summary>
public sealed record RecordEditorView(Guid AccountId, string Name, DateTimeOffset StartedAt);
