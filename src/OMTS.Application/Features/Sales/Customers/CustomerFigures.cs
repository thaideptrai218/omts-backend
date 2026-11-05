using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>The money figures of a customer profile, read from one place.</summary>
internal static class CustomerFigures
{
    /// <summary>
    /// The customer debt (BR-DEBT-02) is worked out from invoices and payments. Neither exists before SALE-05 and DEBT-02,
    /// so it is 0 for every profile; those PRs replace this with the real sum.
    /// </summary>
    public static decimal CurrentDebt(Customer _) => 0m;
}
