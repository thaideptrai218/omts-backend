namespace OMTS.Application.Features.Sales.Invoices.Dtos;

/// <summary>One line as the trader reads it: what was bought, the kg of each cage and the price paid, without the price list or who decided the price.</summary>
/// <param name="CageKgs">The kg of each cage in the order weighed; empty for a line sold by the kg.</param>
public sealed record MyInvoiceLineView(int LineNumber, string ProductName, string LotCode, decimal Kg, decimal UnitPrice, decimal Amount, IReadOnlyList<decimal> CageKgs);
