namespace OMTS.Application.Features.Sales.Invoices.Dtos;

/// <summary>One line as the trader reads it: what was bought and the price paid, without the price list or who decided the price.</summary>
public sealed record MyInvoiceLineView(int LineNumber, string ProductName, string LotCode, decimal Kg, decimal UnitPrice, decimal Amount);
