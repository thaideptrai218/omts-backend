using Microsoft.Extensions.Configuration;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>
/// "Bootstrap" secrets for the one-time deployment command. Values come from user-secrets, environment
/// variables or the hosting secret store, never from the repository.
/// </summary>
public sealed record BootstrapSettings(
    string? CooperativeFullName,
    string? OwnerDisplayName,
    string? OwnerUsername,
    string? OwnerPhoneNumber,
    string? OwnerEmail,
    string? OwnerTemporaryPassword,
    bool SeedDemoData)
{
    public const string CooperativeFullNameKey = "Bootstrap:Cooperative:FullName";
    public const string OwnerDisplayNameKey = "Bootstrap:Owner:DisplayName";
    public const string OwnerUsernameKey = "Bootstrap:Owner:Username";
    public const string OwnerPhoneNumberKey = "Bootstrap:Owner:PhoneNumber";
    public const string OwnerEmailKey = "Bootstrap:Owner:Email";
    public const string OwnerTemporaryPasswordKey = "Bootstrap:Owner:TemporaryPassword";
    public const string SeedDemoDataKey = "Bootstrap:SeedDemoData";

    public static BootstrapSettings From(IConfiguration configuration) => new(
        configuration[CooperativeFullNameKey],
        configuration[OwnerDisplayNameKey],
        configuration[OwnerUsernameKey],
        configuration[OwnerPhoneNumberKey],
        configuration[OwnerEmailKey],
        configuration[OwnerTemporaryPasswordKey],
        configuration.GetValue<bool>(SeedDemoDataKey));

    /// <summary>Names (never values) of the required keys that are not set.</summary>
    public IReadOnlyList<string> MissingKeys() =>
        new (string Key, string? Value)[]
            {
                (CooperativeFullNameKey, CooperativeFullName),
                (OwnerDisplayNameKey, OwnerDisplayName),
                (OwnerUsernameKey, OwnerUsername),
                (OwnerPhoneNumberKey, OwnerPhoneNumber),
                (OwnerTemporaryPasswordKey, OwnerTemporaryPassword),
            }
            .Where(x => string.IsNullOrWhiteSpace(x.Value))
            .Select(x => x.Key)
            .ToList();
}
