namespace OMTS.Application.Features.Testing;

public static class TestRoleNames
{
    public static string Username(Guid ownerId, string role) => "dev_" + ownerId.ToString("N") + "_" + role.ToLowerInvariant();
    public static bool TryOwnerId(string username, out Guid ownerId)
    {
        ownerId = Guid.Empty;
        return username.Length > 37 && username.StartsWith("dev_", StringComparison.Ordinal) && username[36] == '_' &&
            username[37..] is "trader" or "zonemanager" or "employee" &&
            Guid.TryParseExact(username.Substring(4, 32), "N", out ownerId);
    }
}
