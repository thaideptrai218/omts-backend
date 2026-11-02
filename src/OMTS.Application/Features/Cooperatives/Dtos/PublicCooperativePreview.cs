
namespace OMTS.Application.Features.Cooperatives.Dtos;

/// <summary>Exactly what the public provenance page shows about the cooperative (FARM-01).</summary>
public sealed record PublicCooperativePreview(string Name, string? OfficeAddress, string? LogoUrl);
