using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

internal sealed record DemoReceiptLine(string Item, decimal Quantity, decimal UnitPrice, string? SupplierLotCode = null, string? ActualSource = null);
