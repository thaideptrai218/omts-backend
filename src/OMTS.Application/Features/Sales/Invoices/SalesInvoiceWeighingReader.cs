using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// An invoice made from a confirmed weighing takes its kg from it (decision of 09/11/2026): each line names one lot of the weighing, every lot is on one line, and a kg sent has to be the
/// weighed one. Whether the weighing is confirmed and still free is read on each attempt of the save (<see cref="SalesInvoiceFactsReader"/>).
/// </summary>
public sealed class SalesInvoiceWeighingReader(IPortWeighingRepository weighings)
{
    internal async Task<Result<CreateSalesInvoiceRequest>> FillKgAsync(CreateSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (request.PortWeighingId is not { } weighingId || request.Lines is null)
        {
            return request;
        }

        var weighing = await weighings.GetAsync(weighingId, cancellationToken);
        if (weighing is null || weighing.PurchaseRequestId != request.PurchaseRequestId)
        {
            return AppError.Invalid("Port weighing");
        }

        var kgOfLot = weighing.Cages.GroupBy(c => c.HarvestLotId).ToDictionary(g => g.Key, g => g.Sum(c => c.Kg));
        var used = new HashSet<Guid>();
        var invalid = new List<string>();
        var filled = new List<SalesInvoiceLineEntry?>();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line?.HarvestLotId is not { } lotId)
            {
                filled.Add(line);
                continue;
            }

            if (!kgOfLot.TryGetValue(lotId, out var kg) || !used.Add(lotId))
            {
                invalid.Add($"Line {i + 1} harvest lot");
            }
            else if (line.Kg is { } sent && sent != kg)
            {
                invalid.Add($"Line {i + 1} kg");
            }

            filled.Add(line with { Kg = kg });
        }

        if (invalid.Count == 0 && used.Count != kgOfLot.Count)
        {
            invalid.Add("Port weighing");
        }

        return invalid.Count > 0 ? AppError.InvalidFields(invalid) : request with { Lines = filled };
    }
}
