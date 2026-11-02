using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

internal enum DemoReceiptOutcome
{
    Draft,
    Completed,

    /// <summary>Completed first, then cancelled with a reason (its payments are cancelled too).</summary>
    Cancelled,
}
