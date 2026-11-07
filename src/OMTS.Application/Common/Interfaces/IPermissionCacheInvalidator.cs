namespace OMTS.Application.Common.Interfaces;

/// <summary>Drops cached permissions so role and grant changes apply immediately (BR-19).</summary>
public interface IPermissionCacheInvalidator
{
    void InvalidateAccount(Guid accountId);

    /// <summary>A role's permissions changed; it may be held by any account.</summary>
    void InvalidateAll();
}
