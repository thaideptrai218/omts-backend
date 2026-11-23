namespace OMTS.Application.Features.Sales.Checkout.Dtos;

/// <summary>A started checkout: <paramref name="Created"/> is false when the checkout still being paid was handed out again (the API answers 200 instead of 201).</summary>
public sealed record TraderCheckoutStarted(bool Created, TraderCheckoutView Checkout);
