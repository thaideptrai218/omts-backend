namespace OMTS.Api.Filters;

/// <summary>Endpoint stays open while the account must change its temporary password (UC04 endpoints).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhenPasswordChangeRequiredAttribute : Attribute;
