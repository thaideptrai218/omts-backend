using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;

namespace Omts.Api.Hosting;

public sealed class AllowedHostsValidator(IHostEnvironment environment) : IValidateOptions<HostFilteringOptions>
{
    public ValidateOptionsResult Validate(string? name, HostFilteringOptions options)
    {
        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        // Host filtering is disabled by catch-all hosts. Require explicit names outside development.
        if (options.AllowedHosts.Count == 0 || options.AllowedHosts.Any(host =>
            string.IsNullOrWhiteSpace(host) || host.Contains('*') || host is "0.0.0.0" or "[::]" ||
            Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown))
        {
            return ValidateOptionsResult.Fail(
                "Set AllowedHosts to explicit semicolon-separated host names (without ports) outside Development. Wildcards are forbidden.");
        }

        return ValidateOptionsResult.Success;
    }
}
