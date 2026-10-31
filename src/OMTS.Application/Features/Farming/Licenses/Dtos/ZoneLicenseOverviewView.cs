using OMTS.Application.Features.Employees;

namespace OMTS.Application.Features.Farming.Licenses.Dtos;

public sealed record ZoneLicenseOverviewView(Guid ZoneId, DateOnly AsOf, IReadOnlyList<ZoneLicenseRowView> Licenses);
