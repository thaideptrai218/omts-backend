using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.Commands;
using OMTS.Infrastructure.Bootstrap;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>How <c>dotnet OMTS.Api.dll</c> tells the bootstrap command from a normal web host start.</summary>
public class BootstrapCommandTests
{
    [Theory]
    [InlineData(new[] { "seed", "--bootstrap" }, new string[0])]
    [InlineData(new[] { "--environment", "Production", "seed", "--bootstrap" }, new[] { "--environment", "Production" })]
    [InlineData(new[] { "seed", "--environment", "Staging", "--bootstrap", "--demo" }, new[] { "--environment", "Staging" })]
    public void Command_is_found_anywhere_and_its_words_are_not_passed_to_the_host(string[] args, string[] hostArgs)
    {
        Assert.True(BootstrapCommand.IsInvoked(args));
        Assert.Equal(hostArgs, BootstrapCommand.HostArguments(args));
    }

    [Fact]
    public void Normal_start_is_not_the_command_and_keeps_its_arguments()
    {
        string[] args = ["--environment", "Production", "--urls", "http://+:8080"];

        Assert.False(BootstrapCommand.IsInvoked(args));
        Assert.Equal(args, BootstrapCommand.HostArguments(args));
    }

    // A flag without the command (or the command without the flag) fails with the usage text instead of
    // silently starting the web host.
    [Theory]
    [InlineData("--bootstrap")]
    [InlineData("seed")]
    public async Task Incomplete_command_fails_with_exit_code_1(string onlyWord)
    {
        string[] args = [onlyWord];
        Assert.True(BootstrapCommand.IsInvoked(args));
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(DeploymentBootstrapper.Failure, await BootstrapCommand.RunAsync(services, args));
    }
}
