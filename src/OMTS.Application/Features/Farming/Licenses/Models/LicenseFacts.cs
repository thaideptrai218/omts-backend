
namespace OMTS.Application.Features.Farming.Licenses.Models;

/// <summary>The facts the state of one license depends on.</summary>
public sealed record LicenseFacts(DateOnly? EffectiveDate, DateOnly? ExpiryDate, IReadOnlyList<DateOnly?> ReplacementEffectiveDates);
