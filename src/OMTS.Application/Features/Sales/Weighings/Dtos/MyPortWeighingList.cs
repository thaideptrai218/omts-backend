using OMTS.Application.Features.AccessManagement.Dtos;

namespace OMTS.Application.Features.Sales.Weighings.Dtos;

/// <param name="Page">The rows of the page, newest first.</param>
/// <param name="Message">MSG-GEN-11 when the trader has no weighing yet.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record MyPortWeighingList(Paged<PortWeighingView> Page, string? Message, string? MessageCode);
