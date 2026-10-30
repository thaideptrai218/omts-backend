using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>A partial return (INP-05) of one line: refund plus debt reduction equal the returned value.</summary>
internal sealed record DemoReturn(string Item, decimal Quantity, int DaysAgo, decimal Refund, decimal DebtReduction, string Reason);
