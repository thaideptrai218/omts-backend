namespace OMTS.Api.Filters;

/// <summary>Endpoint stays open while a trader/staff account has no verified phone (verification endpoints).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhenPhoneUnverifiedAttribute : Attribute;
