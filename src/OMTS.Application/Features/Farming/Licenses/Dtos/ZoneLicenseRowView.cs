using OMTS.Application.Features.Employees;

namespace OMTS.Application.Features.Farming.Licenses.Dtos;

/// <summary>One row of a zone's license overview; a Missing row has no document.</summary>
public sealed record ZoneLicenseRowView(
    string DocumentType,
    string DocumentTypeName,
    LicenseState State,
    Guid? DocumentId,
    string? DocumentNumber,
    DateOnly? EffectiveDate,
    DateOnly? ExpiryDate,
    bool IsReplaced);
