using OMTS.Infrastructure.Bootstrap;

namespace OMTS.Api.Commands;

/// <summary>
/// <c>dotnet OMTS.Api.dll seed --bootstrap [--demo]</c>, run on the server by the deployment operator
/// before the web host starts; it is not reachable over HTTP.
/// </summary>
internal static class BootstrapCommand
{
    public const string Name = "seed";
    private const string BootstrapFlag = "--bootstrap";
    private const string DemoFlag = "--demo";

    public static bool IsInvoked(string[] args) => args.Length > 0 && args[0] == Name;

    /// <summary>Arguments left for the host (e.g. --environment), without the command and its flags.</summary>
    public static string[] HostArguments(string[] args) =>
        IsInvoked(args) ? args.Skip(1).Where(a => a is not (BootstrapFlag or DemoFlag)).ToArray() : args;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (!args.Contains(BootstrapFlag))
        {
            Console.Error.WriteLine($"Usage: dotnet OMTS.Api.dll {Name} {BootstrapFlag} [{DemoFlag}]");
            return DeploymentBootstrapper.Failure;
        }

        try
        {
            using var scope = services.CreateScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>()
                .RunAsync(args.Contains(DemoFlag), CancellationToken.None);

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
            Console.Error.WriteLine($"Bootstrap failed: {cause.GetType().Name}: {cause.Message}");
            return DeploymentBootstrapper.Failure;
        }
    }
}
