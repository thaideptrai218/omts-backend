using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OMTS.Application.Common.Validation;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// The one place that decides whether <c>seed --bootstrap --demo</c> may run here, and with which password. Called
/// before anything is written. Development: yes, with the well-known password of the README. Staging (a shared demo
/// server): only with <see cref="AllowOutsideDevelopmentKey"/> and a secret <see cref="PasswordKey"/> that is not
/// well-known. Production and any other environment name: never, whatever the configuration says. Refusals name the
/// keys, never a value.
/// </summary>
internal static class DemoSeedGate
{
    public const string AllowOutsideDevelopmentKey = "Demo:AllowOutsideDevelopment";
    public const string PasswordKey = "Demo:Password";

    /// <summary>The demo password of local Development, printed in the README. Refused everywhere else.</summary>
    public const string WellKnownPassword = "Demo@1234";

    private const string Guide = "see README \"Demo on a shared server\"";

    public static (string? Password, string? Refusal) Check(IHostEnvironment environment, IConfiguration configuration)
    {
        if (environment.IsDevelopment())
        {
            return (WellKnownPassword, null);
        }

        if (!environment.IsStaging())
        {
            return (null, "Demo data is refused outside Development and Staging; no setting opens it in this environment.");
        }

        // Anything but a plain true (unset, "1", "yes") stays closed.
        if (!bool.TryParse(configuration[AllowOutsideDevelopmentKey], out var allowed) || !allowed)
        {
            return (null, $"Demo data is refused in Staging unless {AllowOutsideDevelopmentKey} is true; {Guide}.");
        }

        var password = configuration[PasswordKey];
        return PasswordProblem(password) is { } problem
            ? (null, $"Demo data is refused in Staging: {PasswordKey} {problem}; {Guide}.")
            : (password, null);
    }

    private static string? PasswordProblem(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "is not set";
        }

        // A stray newline from a secret store would leave nobody able to log in.
        if (password != password.Trim())
        {
            return "must not start or end with whitespace";
        }

        if (string.Equals(password, WellKnownPassword, StringComparison.OrdinalIgnoreCase))
        {
            return "must not be the well-known password of the local demo";
        }

        return PasswordPolicy.IsValid(password)
            ? null
            : $"must have {PasswordPolicy.MinimumLength} to {PasswordPolicy.MaximumLength} characters with a letter and a digit";
    }
}
