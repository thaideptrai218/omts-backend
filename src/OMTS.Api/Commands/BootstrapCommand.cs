using OMTS.Infrastructure.Bootstrap;

namespace OMTS.Api.Commands;

/// <summary>
/// <c>dotnet OMTS.Api.dll seed --bootstrap [--demo]</c> and <c>dotnet OMTS.Api.dll seed --reset-owner-password</c>,
/// run on the server by the deployment operator before the web host starts; not reachable over HTTP.
/// </summary>
internal static class BootstrapCommand
{
    public const string Name = "seed";
    private const string BootstrapFlag = "--bootstrap";
    private const string DemoFlag = "--demo";
    private const string ResetOwnerPasswordFlag = "--reset-owner-password";

    private static readonly string[] Words = [Name, BootstrapFlag, DemoFlag, ResetOwnerPasswordFlag];

    // Anywhere in the arguments (host options such as --environment may come first). A flag on its own also
    // counts, so a mistyped command fails with the usage text instead of starting the web host.
    public static bool IsInvoked(string[] args) => args.Contains(Name) || args.Contains(BootstrapFlag) || args.Contains(ResetOwnerPasswordFlag);

    /// <summary>Arguments left for the host (e.g. --environment), without the command and its flags.</summary>
    public static string[] HostArguments(string[] args) =>
        IsInvoked(args) ? args.Where(a => !Words.Contains(a)).ToArray() : args;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var bootstrap = args.Contains(BootstrapFlag);
        var reset = args.Contains(ResetOwnerPasswordFlag);
        if (!args.Contains(Name) || bootstrap == reset || (reset && args.Contains(DemoFlag)))
        {
            Console.Error.WriteLine($"Usage: dotnet OMTS.Api.dll {Name} {BootstrapFlag} [{DemoFlag}]");
            Console.Error.WriteLine($"   or: dotnet OMTS.Api.dll {Name} {ResetOwnerPasswordFlag}");
            return DeploymentBootstrapper.Failure;
        }

        try
        {
            using var scope = services.CreateScope();
            var outcome = reset
                ? await scope.ServiceProvider.GetRequiredService<OwnerPasswordReset>().RunAsync(CancellationToken.None)
                : await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(args.Contains(DemoFlag), CancellationToken.None);

            var output = outcome.ExitCode == DeploymentBootstrapper.Success ? Console.Out : Console.Error;
            foreach (var message in outcome.Messages)
            {
                output.WriteLine(message);
            }

            return outcome.ExitCode;
        }
        catch (Exception exception)
        {
            // The innermost cause (e.g. a duplicate username) is what the operator needs; the password is
            // only ever held hashed, so no secret can appear here.
            var cause = exception.GetBaseException();
            Console.Error.WriteLine($"{(reset ? "Owner password reset" : "Bootstrap")} failed: {cause.GetType().Name}: {cause.Message}");
            return DeploymentBootstrapper.Failure;
        }
    }
}
