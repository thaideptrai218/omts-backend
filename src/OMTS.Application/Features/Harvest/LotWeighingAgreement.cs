namespace OMTS.Application.Features.Harvest;

/// <summary>The trader's agreement to the weighing the lot has now.</summary>
/// <param name="AgreedKg">The weighed kg the trader agreed to, the cooperative's figure.</param>
public sealed record LotWeighingAgreement(DateTimeOffset AgreedAt, decimal AgreedKg);
