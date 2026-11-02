using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Licenses.Models;

/// <summary>One row of a zone's license overview: a covering document with its state, or a Missing row (no document).</summary>
public sealed record ZoneLicenseRow(string DocumentType, ZoneComplianceDocument? Document, bool IsReplaced, LicenseState State);
