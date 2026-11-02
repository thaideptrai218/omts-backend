using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>A payment recorded after the receipt (INP-06), <paramref name="DaysAgo"/> days before today.</summary>
internal sealed record DemoLaterPayment(int DaysAgo, decimal Amount, PaymentMethod Method);
