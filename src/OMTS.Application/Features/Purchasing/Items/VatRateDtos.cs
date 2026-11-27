namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>A VAT rate of the list ("Thuế GTGT").</summary>
/// <param name="Percent">0 to 100; null only on KCT.</param>
/// <param name="IsExempt">KCT: not subject to VAT.</param>
/// <param name="IsSystem">KCT: not editable, not deactivatable.</param>
/// <param name="IsDefault">The rate a new supply item starts with.</param>
/// <param name="Active">An inactive rate is out of the pickers; the lines made with it still name it.</param>
/// <param name="ItemCount">How many items (not deleted) have the rate.</param>
public sealed record VatRateView(
    Guid Id, string Name, decimal? Percent, bool IsExempt, bool IsSystem, bool IsDefault, bool Active, int SortOrder, int ItemCount, string RowVersion);

/// <param name="Name">1 to 50 characters, unique regardless of letter case (409 MSG-GEN-02 on <c>Name</c>).</param>
/// <param name="Percent">0 to 100, at most 2 decimals.</param>
/// <param name="RowVersion">Required on an update; ignored on a create.</param>
public sealed record SaveVatRateRequest(string? Name, decimal? Percent, string? RowVersion = null);

/// <param name="ReplacementId">The active rate the items of this rate move to; required while items have the rate.</param>
public sealed record DeactivateVatRateRequest(Guid? ReplacementId, string? RowVersion);

/// <param name="MovedItems">How many items moved to the replacement.</param>
public sealed record DeactivateVatRateResult(VatRateView Rate, int MovedItems);
