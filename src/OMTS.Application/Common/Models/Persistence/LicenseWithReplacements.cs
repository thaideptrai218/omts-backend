using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Models.Persistence;

/// <summary>A license with the effective dates of the documents that replace it (FARM-03 Superseded rule).</summary>
public sealed record LicenseWithReplacements(ZoneComplianceDocument Document, IReadOnlyList<DateOnly?> ReplacementEffectiveDates, bool IsReplaced);
