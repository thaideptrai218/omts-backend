using OMTS.Application.Common.Concurrency;
using OMTS.Application.Features.Sales.Weighings.Dtos;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Weighings;

/// <summary>A weighing as the API shows it, from <see cref="Common.Interfaces.Persistence.IPortWeighingRepository.GetAsync"/>.</summary>
internal static class PortWeighingViews
{
    public static PortWeighingView Of(PortWeighing weighing)
    {
        var lots = weighing.Cages.OrderBy(c => c.CageNumber).GroupBy(c => c.HarvestLotId)
            .Select(g => new PortWeighingLotView(g.Key, g.First().HarvestLot.LotCode, g.Sum(c => c.Kg), g.Select(c => c.Kg).ToList()))
            .ToList();
        var request = weighing.PurchaseRequest;
        var invoice = weighing.Invoices.FirstOrDefault();
        return new PortWeighingView(
            weighing.Id, request.Id, request.Code, request.CustomerId, request.Customer.Name, request.ProductId, request.ProductName, request.AcceptedCages ?? 0, weighing.WeighedAt, lots,
            lots.Sum(l => l.CageKgs.Count), lots.Sum(l => l.Kg), weighing.ConfirmedAt, invoice?.Id, invoice?.Code, RowVersionGuard.Encode(weighing.RowVersion));
    }
}
